using './main.bicep'

param environmentName = 'dev'
param location = 'eastus'
param quotesApiExists = true

param principalId = readEnvironmentVariable('AZURE_PRINCIPAL_ID')
param principalType = readEnvironmentVariable('AZURE_PRINCIPAL_TYPE', 'User')
param jwtSigningKey = readEnvironmentVariable('AZURE_JWT_SIGNING_KEY')

param deployDataServices = true

param apiSettings = {
  minReplicas: 0
  maxReplicas: 2
}

param sqlSettings = {
  skuName: 'Basic'
  skuTier: 'Basic'
  maxSizeBytes: 2147483648
  backupStorageRedundancy: 'Local'
}

// Basic carries queues only, so Standard is the floor for a topic rather than a choice.
param serviceBusSettings = {
  sku: 'Standard'
  maxDeliveryCount: 5
  messageTimeToLive: 'P1D'
}
