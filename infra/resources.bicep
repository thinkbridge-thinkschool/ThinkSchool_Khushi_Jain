@description('The location used for all deployed resources')
param location string = resourceGroup().location

@description('Tags that will be applied to all resources')
param tags object = {}


param quotesApiExists bool

@description('Id of the user or app to assign application roles')
param principalId string

@description('Principal type of user or app')
param principalType string

@secure()
@minLength(32)
@description('HS256 signing key for the API\'s own JWTs')
param jwtSigningKey string

@description('Per-environment settings for the API, passed straight through to its module')
param apiSettings object = {}

var abbrs = loadJsonContent('./abbreviations.json')
var resourceToken = uniqueString(subscription().id, resourceGroup().id, location)

// Role assignment names must be computable before the deployment starts, which
// rules out deriving them from a module output such as the identity's
// principalId. Naming the identity up front keeps that name available.
var quotesApiIdentityName = '${abbrs.managedIdentityUserAssignedIdentities}quotesApi-${resourceToken}'

// Monitor application with Azure Monitor
module monitoring 'br/public:avm/ptn/azd/monitoring:0.1.0' = {
  name: 'monitoring'
  params: {
    logAnalyticsName: '${abbrs.operationalInsightsWorkspaces}${resourceToken}'
    applicationInsightsName: '${abbrs.insightsComponents}${resourceToken}'
    applicationInsightsDashboardName: '${abbrs.portalDashboards}${resourceToken}'
    location: location
    tags: tags
  }
}
// Container registry
module containerRegistry 'br/public:avm/res/container-registry/registry:0.1.1' = {
  name: 'registry'
  params: {
    name: '${abbrs.containerRegistryRegistries}${resourceToken}'
    location: location
    tags: tags
    publicNetworkAccess: 'Enabled'
    roleAssignments:[
      {
        principalId: quotesApiIdentity.outputs.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
      }
    ]
  }
}

// Container apps environment
module containerAppsEnvironment 'br/public:avm/res/app/managed-environment:0.4.5' = {
  name: 'container-apps-environment'
  params: {
    logAnalyticsWorkspaceResourceId: monitoring.outputs.logAnalyticsWorkspaceResourceId
    name: '${abbrs.appManagedEnvironments}${resourceToken}'
    location: location
    zoneRedundant: false
  }
}

module quotesApiIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.2.1' = {
  name: 'quotesApiidentity'
  params: {
    name: quotesApiIdentityName
    location: location
  }
}
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${abbrs.keyVaultVaults}${resourceToken}'
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

// The .NET Key Vault configuration provider maps a double dash in a secret
// name to the ':' separator, so this secret arrives as Jwt:SigningKey.
resource jwtSigningKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'Jwt--SigningKey'
  properties: {
    value: jwtSigningKey
  }
}

// Key Vault Secrets User, scoped to this vault only, so the container's
// managed identity can read secrets at startup without a standing access
// policy or any credential of its own.
resource quotesApiKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, quotesApiIdentityName, '4633458b-17de-408a-b874-0445c86b69e6')
  properties: {
    principalId: quotesApiIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

module quotesApi './modules/api.bicep' = {
  name: 'quotesApi'
  params: {
    location: location
    tags: tags
    exists: quotesApiExists
    environmentResourceId: containerAppsEnvironment.outputs.resourceId
    containerRegistryLoginServer: containerRegistry.outputs.loginServer
    identityResourceId: quotesApiIdentity.outputs.resourceId
    identityClientId: quotesApiIdentity.outputs.clientId
    applicationInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
    keyVaultUri: keyVault.properties.vaultUri
    settings: apiSettings
  }
  // Nothing in the app's definition references these, so without it the app can start before it may read the vault.
  dependsOn: [
    quotesApiKeyVaultAccess
    jwtSigningKeySecret
  ]
}
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = containerRegistry.outputs.loginServer
output AZURE_RESOURCE_QUOTES_API_ID string = quotesApi.outputs.resourceId
