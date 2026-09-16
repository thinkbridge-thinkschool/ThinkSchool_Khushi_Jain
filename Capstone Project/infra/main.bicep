@description('Location for every resource in this template')
param location string = resourceGroup().location

@description('Short name mixed into every resource name')
param applicationName string = 'docbook'

@description('Object id of the Entra principal that deploys this and administers the SQL server')
param administratorPrincipalId string

@description('Kind of that principal, one of User, Group or Application')
param administratorPrincipalType string = 'User'

// Enabled exists only for the first deploy, while the grant and the secret are set from outside.
@allowed(['Enabled', 'Disabled'])
@description('Whether the SQL server and the vault answer on their public endpoints')
param publicNetworkAccess string = 'Disabled'

// The database is the only part of this the happy path needs, so it can be brought up on its own.
@description('Whether to deploy the application tier as well as the database')
param deployApplication bool = true

@description('One address let through the SQL firewall, for an API run outside Azure')
param developerIpAddress string = ''

@description('Where Communication Services keeps email data, which is not always where it is sent from')
param emailDataLocation string = 'United States'

var resourceToken = uniqueString(resourceGroup().id, applicationName)
var databaseName = 'docbook'
var apiSiteName = 'app-${applicationName}-${resourceToken}'
var keyVaultName = 'kv${take(replace(applicationName, '-', ''), 8)}${take(resourceToken, 12)}'

var sqlPrivateZoneName = 'privatelink${environment().suffixes.sqlServerHostname}'
var vaultPrivateZoneName = 'privatelink.vaultcore.azure.net'

var secretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var secretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource network 'Microsoft.Network/virtualNetworks@2023-11-01' = if (deployApplication) {
  name: 'vnet-${applicationName}-${resourceToken}'
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: ['10.20.0.0/16']
    }
    subnets: [
      {
        name: 'app'
        properties: {
          addressPrefix: '10.20.1.0/24'
          // Regional integration hands the whole subnet to the plan, so nothing else may sit here.
          delegations: [
            {
              name: 'serverFarms'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
      {
        name: 'data'
        properties: {
          addressPrefix: '10.20.2.0/24'
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-${applicationName}-${resourceToken}'
  location: location
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: publicNetworkAccess
    // Entra only, so no SQL login exists and there is no password to store or rotate.
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: administratorPrincipalType
      login: administratorPrincipalId
      sid: administratorPrincipalId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
  properties: {
    maxSizeBytes: 2147483648
  }
}

// Until the application tier exists the API runs outside Azure, so it arrives on the public endpoint.
resource developerAccess 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (!empty(developerIpAddress)) {
  parent: sqlServer
  name: 'developer'
  properties: {
    startIpAddress: developerIpAddress
    endIpAddress: developerIpAddress
  }
}

// Not gated on deployApplication: an API running outside Azure still has patients to write to.
resource emailService 'Microsoft.Communication/emailServices@2023-04-01' = {
  name: 'acsmail-${applicationName}-${resourceToken}'
  location: 'global'
  properties: {
    dataLocation: emailDataLocation
  }
}

// Azure owns the domain, so nothing has to be proved with a DNS record before the first send.
resource managedDomain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  parent: emailService
  name: 'AzureManagedDomain'
  location: 'global'
  properties: {
    domainManagement: 'AzureManaged'
    userEngagementTracking: 'Disabled'
  }
}

// Billed per message, so this and the two above cost nothing while no appointment is booked.
resource communicationService 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: 'acs-${applicationName}-${resourceToken}'
  location: 'global'
  properties: {
    dataLocation: emailDataLocation
    linkedDomains: [managedDomain.id]
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = if (deployApplication) {
  name: keyVaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: publicNetworkAccess
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
    }
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = if (deployApplication) {
  name: 'plan-${applicationName}-${resourceToken}'
  location: location
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource api 'Microsoft.Web/sites@2023-12-01' = if (deployApplication) {
  name: apiSiteName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    // The app's half of the private endpoint: without it it resolves the public name and fails.
    virtualNetworkSubnetId: network.properties.subnets[0].id
    vnetRouteAllEnabled: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'KeyVault__Uri'
          value: keyVault.properties.vaultUri
        }
        // No password: the client fetches an Entra token for the app's own identity.
        {
          name: 'ConnectionStrings__DocBook'
          value: join([
            'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433'
            'Database=${database.name}'
            'Encrypt=True'
            'TrustServerCertificate=False'
            'Authentication=Active Directory Default'
          ], ';')
        }
      ]
    }
  }
}

module sqlPrivateLink './modules/private-endpoint.bicep' = if (deployApplication) {
  name: 'sql-private-endpoint'
  params: {
    location: location
    name: 'pe-sql-${resourceToken}'
    subnetId: network.properties.subnets[1].id
    virtualNetworkId: network.id
    targetResourceId: sqlServer.id
    groupId: 'sqlServer'
    privateDnsZoneName: sqlPrivateZoneName
  }
}

module vaultPrivateLink './modules/private-endpoint.bicep' = if (deployApplication) {
  name: 'vault-private-endpoint'
  params: {
    location: location
    name: 'pe-kv-${resourceToken}'
    subnetId: network.properties.subnets[1].id
    virtualNetworkId: network.id
    targetResourceId: keyVault.id
    groupId: 'vault'
    privateDnsZoneName: vaultPrivateZoneName
  }
}

// Read only, and on this vault only, so the app holds no credential of its own.
resource appVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployApplication) {
  scope: keyVault
  name: guid(keyVault.id, api.id, secretsUserRoleId)
  properties: {
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', secretsUserRoleId)
  }
}

// Writing the signing key is a data-plane call, which RBAC governs separately from deploying.
resource administratorVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployApplication) {
  scope: keyVault
  name: guid(keyVault.id, administratorPrincipalId, secretsOfficerRoleId)
  properties: {
    principalId: administratorPrincipalId
    principalType: administratorPrincipalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', secretsOfficerRoleId)
  }
}

output apiName string = deployApplication ? apiSiteName : ''
output apiHostName string = api.?properties.defaultHostName ?? ''
output sqlServerFullyQualifiedDomainName string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = database.name
output keyVaultName string = deployApplication ? keyVaultName : ''
output communicationServiceName string = communicationService.name
output notificationFromAddress string = 'DoNotReply@${managedDomain.properties.mailFromSenderDomain}'
