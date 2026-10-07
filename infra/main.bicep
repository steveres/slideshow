// Slideshow on Azure: website on Static Web Apps; API on Container Apps with Azure SQL (serverless),
// Blob Storage and App Insights. deploy.ps1 runs this twice: first with containerImage empty (creates
// the registry etc.), then, after pushing the image, with containerImage set. See api/README.md.
targetScope = 'resourceGroup'

@description('Short name used to derive resource names.')
@minLength(3)
@maxLength(12)
param appName string = 'slideshow'

param location string = resourceGroup().location

@description('Region for Azure SQL. Defaults to location; set another when that region is not accepting new SQL servers.')
param sqlLocation string = location

@description('Static Web Apps is offered in fewer regions than the rest; pick the closest of them.')
@allowed(['eastus2', 'centralus', 'westus2', 'westeurope', 'eastasia'])
param webLocation string = 'eastus2'

@description('Full image reference, e.g. <registry>.azurecr.io/slideshow-api:1. Leave empty to deploy infrastructure only.')
param containerImage string = ''

@description('Entra External ID token authority: https://<tenant-subdomain>.ciamlogin.com/<tenant-id>/v2.0')
param authAuthority string

@description('Client id of the API app registration in the External ID tenant (the token audience).')
param apiClientId string

@description('External ID (customer) tenant id, for Microsoft Graph calls.')
param externalTenantId string

@description('Client id of the Graph app registration in the External ID tenant (federated with the managed identity). Empty disables Graph.')
param graphClientId string = ''

@secure()
@description('Only if managed-identity federation is not possible: a client secret for the Graph app. Leave empty to use federation.')
param graphClientSecret string = ''

@description('Extra origins allowed to call the API from a browser (the Static Web App is always allowed), e.g. http://localhost:3000')
param allowedOrigins array = []

@description('Use the Azure SQL free offer (one database per subscription).')
param useSqlFreeLimit bool = true

var suffix = uniqueString(resourceGroup().id)
var names = {
  logs: '${appName}-logs'
  insights: '${appName}-insights'
  identity: '${appName}-api-id'
  registry: toLower(replace('${appName}${suffix}', '-', ''))
  storage: take(toLower(replace('${appName}${suffix}', '-', '')), 24)
  sqlServer: '${appName}-sql-${uniqueString(resourceGroup().id, sqlLocation)}' // region in the hash: a name stays reserved in its first region
  sqlDb: appName
  environment: '${appName}-env'
  app: '${appName}-api'
  web: '${appName}-web'
}

// Built-in role definition ids
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  blobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
}

// ───────────── Monitoring ─────────────

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logs
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: names.insights
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ───────────── Identity ─────────────
// User-assigned so its roles exist before the container app first pulls its image.

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.identity
  location: location
}

// ───────────── Container registry ─────────────

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: names.registry
  location: location
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, roles.acrPull)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ───────────── Blob storage ─────────────

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  #disable-next-line BCP334 // appName (3+) + uniqueString (13) is always long enough
  name: names.storage
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false // Entra auth only
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }       // recover from accidental deletes
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

resource mediaContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'media'
  properties: { publicAccess: 'None' }
}

resource blobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, roles.blobDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ───────────── Azure SQL ─────────────
// Entra-only authentication; the API's identity is the server admin (it runs the migrations).

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: names.sqlServer
  location: sqlLocation
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      principalType: 'Application'
      login: identity.name
      sid: identity.properties.clientId
      tenantId: subscription().tenantId
    }
  }
}

resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: names.sqlDb
  location: sqlLocation
  sku: { name: 'GP_S_Gen5_1', tier: 'GeneralPurpose' }
  properties: {
    autoPauseDelay: 60
    minCapacity: json('0.5')
    useFreeLimit: useSqlFreeLimit
    freeLimitExhaustionBehavior: useSqlFreeLimit ? 'AutoPause' : null
  }
}

// ───────────── Container Apps ─────────────

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: names.environment
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

var sqlConnection = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${names.sqlDb};Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60'

// ───────────── Website ─────────────

resource web 'Microsoft.Web/staticSites@2023-12-01' = {
  name: names.web
  location: webLocation
  sku: { name: 'Free', tier: 'Free' }
  properties: {} // content is uploaded by deploy.ps1, not built from a repository
}

var webOrigin = 'https://${web.properties.defaultHostname}'
// The website is always allowed (slot 0); any extra origins follow.
var extraCorsEnv = [for (origin, i) in allowedOrigins: { name: 'Cors__AllowedOrigins__${i + 1}', value: origin }]
var corsEnv = concat([{ name: 'Cors__AllowedOrigins__0', value: webOrigin }], extraCorsEnv)

var appEnv = concat([
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId } // DefaultAzureCredential → this identity
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights' }
  { name: 'Database__Provider', value: 'SqlServer' }
  { name: 'Database__ConnectionString', value: sqlConnection }
  { name: 'Storage__Provider', value: 'AzureBlob' }
  { name: 'Storage__BlobServiceUri', value: storage.properties.primaryEndpoints.blob }
  { name: 'Storage__ContainerName', value: mediaContainer.name }
  { name: 'Auth__Authority', value: authAuthority }
  { name: 'Auth__Audiences__0', value: apiClientId }
  { name: 'Auth__Audiences__1', value: 'api://${apiClientId}' }
  { name: 'Graph__TenantId', value: empty(graphClientId) ? '' : externalTenantId }
  { name: 'Graph__ClientId', value: graphClientId }
  { name: 'Graph__ManagedIdentityClientId', value: identity.properties.clientId }
  { name: 'Graph__UseManagedIdentityFederation', value: empty(graphClientSecret) ? 'true' : 'false' }
], empty(graphClientSecret) ? [] : [{ name: 'Graph__ClientSecret', secretRef: 'graph-client-secret' }], corsEnv)

var appSecrets = concat(
  [{ name: 'appinsights', value: insights.properties.ConnectionString }],
  empty(graphClientSecret) ? [] : [{ name: 'graph-client-secret', value: graphClientSecret }])

resource app 'Microsoft.App/containerApps@2024-03-01' = if (!empty(containerImage)) {
  name: names.app
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  dependsOn: [acrPull, blobContributor, sqlDb, sqlAllowAzure]
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [{ server: registry.properties.loginServer, identity: identity.id }]
      secrets: appSecrets
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: appEnv
          probes: [
            { type: 'Liveness', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 30 }
            { type: 'Readiness', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 10 }
            // First start runs migrations and may wait for a paused database to resume.
            { type: 'Startup', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 10, failureThreshold: 18 }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 3
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '50' } } }]
      }
    }
  }
}

output registryLoginServer string = registry.properties.loginServer
output registryName string = registry.name
output identityClientId string = identity.properties.clientId
output identityPrincipalId string = identity.properties.principalId
output apiUrl string = empty(containerImage) ? '' : 'https://${app!.properties.configuration.ingress.fqdn}'
output webName string = web.name
output webUrl string = webOrigin
