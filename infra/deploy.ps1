<#
  Builds and deploys everything to Azure.
    1. Registers the Azure resource providers used (needed once on a new subscription).
    2. Creates/updates the infrastructure (registry, storage, SQL, Container Apps environment, website).
    3. Builds the API image with the .NET SDK (no Docker needed) and pushes it to the registry.
    4. Deploys the API container with that image.
    5. Packages the website with production settings and uploads it to Static Web Apps.
  Usage:   .\infra\deploy.ps1 -ResourceGroup slideshow-rg -Location eastus
           .\infra\deploy.ps1 -ResourceGroup slideshow-rg -Location eastus -SkipApi    (website only)
  Needs:   Azure CLI (az login done), .NET 9 SDK, Node.js; infra\main.bicepparam and web\app.config.azure.json.
  Written for Windows PowerShell 5.1.
#>
param(
  [Parameter(Mandatory = $true)][string]$ResourceGroup,
  [string]$Location = 'eastus',
  [switch]$SkipApi
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$template = Join-Path $PSScriptRoot 'main.bicep'
$params = Join-Path $PSScriptRoot 'main.bicepparam'
$web = Join-Path $root 'web'

# Runs an external program and fails only on its exit code. Windows PowerShell 5.1 turns anything a
# program writes to stderr (often just warnings or progress) into an error when output is redirected,
# which would stop this script; such lines are shown instead, and stdout is returned.
function Invoke-Native([string]$Exe, [string[]]$Arguments) {
  $saved = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try {
    $out = & $Exe @Arguments 2>&1 | ForEach-Object {
      if ($_ -is [System.Management.Automation.ErrorRecord]) { Write-Host "  $_" -ForegroundColor DarkGray } else { $_ }
    }
  } finally { $ErrorActionPreference = $saved }
  if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') failed (exit code $LASTEXITCODE)" }
  return ($out -join "`n") # one string: multi-line JSON must reach ConvertFrom-Json whole in PowerShell 5.1
}
function Invoke-Az { return Invoke-Native 'az' $args }

Write-Host '1/5 Resource providers...'
foreach ($ns in 'Microsoft.App', 'Microsoft.ContainerRegistry', 'Microsoft.Sql', 'Microsoft.Storage', 'Microsoft.OperationalInsights',
                'Microsoft.Insights', 'Microsoft.ManagedIdentity', 'Microsoft.Web') {
  $state = Invoke-Az provider show --namespace $ns --query registrationState --output tsv
  if ($state -ne 'Registered') {
    Write-Host "  registering $ns (can take a few minutes)"
    Invoke-Az provider register --namespace $ns --wait --output none | Out-Null
  }
}

Write-Host '2/5 Infrastructure...'
Invoke-Az group create --name $ResourceGroup --location $Location --output none | Out-Null
$out = Invoke-Az deployment group create --resource-group $ResourceGroup --name slideshow-infra --template-file $template --parameters $params `
  --query properties.outputs --output json | ConvertFrom-Json
$loginServer = $out.registryLoginServer.value
$registry = $out.registryName.value

if (-not $SkipApi) {
  Write-Host "3/5 Building and pushing the API image to $loginServer..."
  $tag = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
  $sha = (& git -C $root rev-parse --short HEAD 2>$null)
  if ($LASTEXITCODE -eq 0 -and $sha) { $tag = "$tag-$sha" }
  # The SDK's container tooling reads registry credentials from these variables.
  $env:DOTNET_CONTAINER_REGISTRY_UNAME = '00000000-0000-0000-0000-000000000000'
  $env:DOTNET_CONTAINER_REGISTRY_PWORD = Invoke-Az acr login --name $registry --expose-token --query accessToken --output tsv
  try {
    Invoke-Native 'dotnet' @('publish', (Join-Path $root 'api\src\Slideshow.Api'), '-c', 'Release', '-t:PublishContainer',
      "-p:ContainerRegistry=$loginServer", "-p:ContainerImageTag=$tag") | Write-Host
  } finally {
    Remove-Item Env:DOTNET_CONTAINER_REGISTRY_PWORD -ErrorAction SilentlyContinue
  }

  Write-Host '4/5 Deploying the API...'
  $image = "$loginServer/slideshow-api:$tag"
  $out = Invoke-Az deployment group create --resource-group $ResourceGroup --name slideshow-api --template-file $template --parameters $params `
    --parameters "containerImage=$image" --query properties.outputs --output json | ConvertFrom-Json
} else {
  Write-Host '3-4/5 Skipping the API (-SkipApi).'
}
$apiUrl = $out.apiUrl.value
if (-not $apiUrl) {
  # Infrastructure-only output has no API address; read it from the running app.
  $fqdn = Invoke-Az containerapp show --resource-group $ResourceGroup --name slideshow-api --query properties.configuration.ingress.fqdn --output tsv
  $apiUrl = "https://$fqdn"
}
$webName = $out.webName.value
$webUrl = $out.webUrl.value

Write-Host "5/5 Website -> $webUrl ..."
$staging = Join-Path ([IO.Path]::GetTempPath()) 'slideshow-web-package'
$siteConfig = Get-Content (Join-Path $web 'app.config.azure.json') -Raw | ConvertFrom-Json
$siteConfig | Add-Member -NotePropertyName apiBaseUrl -NotePropertyValue $apiUrl -Force
$env:APP_CONFIG = $siteConfig | ConvertTo-Json -Depth 5 -Compress
Push-Location $web
try {
  if (-not (Test-Path node_modules)) { Invoke-Native 'npm' @('ci') | Write-Host }
  Invoke-Native 'node' @('build.mjs', '--out', $staging) | Write-Host
} finally {
  Pop-Location
  Remove-Item Env:APP_CONFIG -ErrorAction SilentlyContinue
}
$deployToken = Invoke-Az staticwebapp secrets list --name $webName --resource-group $ResourceGroup --query properties.apiKey --output tsv
Invoke-Native 'npx' @('--yes', '@azure/static-web-apps-cli@2', 'deploy', $staging, '--deployment-token', $deployToken, '--env', 'production') | Write-Host

Write-Host ''
Write-Host "Website:          $webUrl"
Write-Host "API:              $apiUrl"
Write-Host "Sign-in redirect: $webUrl/login.html  (must be listed on the 'Slideshow Website' app registration)"
try {
  $health = Invoke-RestMethod "$apiUrl/healthz" -TimeoutSec 120
  Write-Host "API health:       $($health.status)"
} catch {
  Write-Host 'API health:       not answering yet (first start runs migrations and may wake the database); retry in a minute.' -ForegroundColor Yellow
}
