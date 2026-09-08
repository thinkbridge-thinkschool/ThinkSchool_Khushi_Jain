type sqlSettings = {
  location: string?
  skuName: string?
  skuTier: string?
  maxSizeBytes: int?
  zoneRedundant: bool?
  backupStorageRedundancy: string?
  databaseName: string?
  allowAzureServices: bool?
}

@description('The location used for all deployed resources')
param location string

@description('Tags that will be applied to all resources')
param tags object = {}

@description('Name of the logical SQL server')
param name string

@description('Object id of the Entra principal that administers the server')
param administratorPrincipalId string

@description('Kind of that principal, one of User, Group or Application')
param administratorPrincipalType string = 'User'

@description('Environment overrides; anything omitted falls back to the defaults below')
param settings sqlSettings = {}

// SQL keeps its own location because a region can refuse new servers while still accepting everything else.
var defaults = {
  location: location
  skuName: 'Basic'
  skuTier: 'Basic'
  maxSizeBytes: 2147483648
  zoneRedundant: false
  backupStorageRedundancy: 'Local'
  databaseName: 'quotes'
  allowAzureServices: true
}

var sql = union(defaults, settings)

resource server 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: name
  location: sql.location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    // Entra-only, so no SQL login exists and there is no password to store or rotate.
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
  parent: server
  name: sql.databaseName
  location: sql.location
  tags: tags
  sku: {
    name: sql.skuName
    tier: sql.skuTier
  }
  properties: {
    maxSizeBytes: sql.maxSizeBytes
    zoneRedundant: sql.zoneRedundant
    requestedBackupStorageRedundancy: sql.backupStorageRedundancy
  }
}

// 0.0.0.0 on both ends is the sentinel for "any Azure service", not a real range.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (sql.allowAzureServices) {
  parent: server
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output serverName string = server.name
output fullyQualifiedDomainName string = server.properties.fullyQualifiedDomainName
output databaseName string = database.name
