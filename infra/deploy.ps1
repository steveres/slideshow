<#
  Builds the API container and deploys everything to Azure.
    1. Creates/updates the infrastructure (registry, storage, SQL, Container Apps environment).
    2. Builds the image with the .NET SDK (no Docker needed) and pushes it to the registry.
    3. Deploys the container app with that image.
  Usage:   .\infra\deploy.ps1 -ResourceGroup slideshow-rg -Location westus2
  Needs:   Azure CLI (az login done), .NET 9 SDK, infra\main.bicepparam filled in.
  Written for Windows PowerShell 5.1.
#>
param(
  [Parameter(Mandatory = $true)][string]$ResourceGroup,
  [string]$Location = 'westus2'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$template = Join-Path $PSScriptRoot 'main.bicep'
$params = Join-Path $PSScriptRoot 'main.bicepparam'

function Invoke-Az {
  $out = & az @args
  if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed" }
  return $out
}

Write-Host '1/3 Infrastructure...'
Invoke-Az group create --name $ResourceGroup --location $Location --output none | Out-Null
$infra = Invoke-Az deployment group create --resource-group $ResourceGroup --template-file $template --parameters $params `
  --query properties.outputs --output json | ConvertFrom-Json
$loginServer = $infra.registryLoginServer.value
$registry = $infra.registryName.value

Write-Host "2/3 Building and pushing the image to $loginServer..."
$tag = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$sha = (& git -C $root rev-parse --short HEAD 2>$null)
if ($LASTEXITCODE -eq 0 -and $sha) { $tag = "$tag-$sha" }
# The SDK's container tooling reads registry credentials from these variables.
$env:DOTNET_CONTAINER_REGISTRY_UNAME = '00000000-0000-0000-0000-000000000000'
$env:DOTNET_CONTAINER_REGISTRY_PWORD = Invoke-Az acr login --name $registry --expose-token --query accessToken --output tsv
try {
  & dotnet publish (Join-Path $root 'api\src\Slideshow.Api') -c Release -t:PublishContainer `
    "-p:ContainerRegistry=$loginServer" "-p:ContainerImageTag=$tag"
  if ($LASTEXITCODE -ne 0) { throw 'Container build failed.' }
} finally {
  Remove-Item Env:DOTNET_CONTAINER_REGISTRY_PWORD -ErrorAction SilentlyContinue
}

Write-Host '3/3 Deploying the container app...'
$image = "$loginServer/slideshow-api:$tag"
$app = Invoke-Az deployment group create --resource-group $ResourceGroup --template-file $template --parameters $params `
  --parameters "containerImage=$image" --query properties.outputs --output json | ConvertFrom-Json
$url = $app.apiUrl.value

Write-Host ''
Write-Host "API:              $url"
Write-Host "Image:            $image"
Write-Host "Managed identity: $($app.identityPrincipalId.value) (object id, for the Graph federated credential)"
try {
  $health = Invoke-RestMethod "$url/healthz" -TimeoutSec 120
  Write-Host "Health:           $($health.status)"
} catch {
  Write-Host 'Health:           not answering yet (first start runs migrations and may wake the database); retry in a minute.' -ForegroundColor Yellow
}
