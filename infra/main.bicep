targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment that can be used as part of naming resource convention')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
param location string


param quotesApiExists bool

@description('Id of the user or app to assign application roles')
param principalId string

@description('Principal type of user or app')
param principalType string

@secure()
@minLength(32)
@description('HS256 signing key for the API\'s own JWTs. Stored in Key Vault and read from there at startup; never written to configuration files.')
param jwtSigningKey string

@description('Per-environment settings for the API. Set only what differs from the module\'s defaults; see modules/api.bicep for the available properties.')
param apiSettings object = {}

@description('Per-environment settings for the database. See modules/sql.bicep for the available properties.')
param sqlSettings object = {}

@description('Per-environment settings for Service Bus. See modules/servicebus.bicep for the available properties.')
param serviceBusSettings object = {}

@description('Create the database and the Service Bus namespace. Off by default because both bill from the moment they exist and the API still reads SQLite.')
param deployDataServices bool = false

@description('Create the Container Apps environment and the API container app. The subscription allows one Container App Environment in total, so a second environment must leave this off.')
param deployContainerApp bool = true

// Tags that should be applied to all resources.
// 
// Note that 'azd-service-name' tags should be applied separately to service host resources.
// Example usage:
//   tags: union(tags, { 'azd-service-name': <service name in azure.yaml> })
var tags = {
  'azd-env-name': environmentName
}

// Organize resources in a resource group
resource rg 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module resources 'resources.bicep' = {
  scope: rg
  name: 'resources'
  params: {
    location: location
    tags: tags
    principalId: principalId
    principalType: principalType
    quotesApiExists: quotesApiExists
    jwtSigningKey: jwtSigningKey
    apiSettings: apiSettings
    sqlSettings: sqlSettings
    serviceBusSettings: serviceBusSettings
    deployDataServices: deployDataServices
    deployContainerApp: deployContainerApp
  }
}
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = resources.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output AZURE_RESOURCE_QUOTES_API_ID string = resources.outputs.AZURE_RESOURCE_QUOTES_API_ID
