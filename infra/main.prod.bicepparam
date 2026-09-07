using './main.bicep'

param environmentName = 'prod'
param location = 'eastus'
param quotesApiExists = false

param principalId = readEnvironmentVariable('AZURE_PRINCIPAL_ID')
param principalType = readEnvironmentVariable('AZURE_PRINCIPAL_TYPE', 'User')
param jwtSigningKey = readEnvironmentVariable('AZURE_JWT_SIGNING_KEY')

param deployDataServices = true

param apiSettings = {
  minReplicas: 2
  maxReplicas: 10
}

param sqlSettings = {
  skuName: 'S1'
  skuTier: 'Standard'
  maxSizeBytes: 268435456000
  backupStorageRedundancy: 'Geo'
}

param serviceBusSettings = {
  sku: 'Standard'
  maxDeliveryCount: 10
  messageTimeToLive: 'P7D'
}
