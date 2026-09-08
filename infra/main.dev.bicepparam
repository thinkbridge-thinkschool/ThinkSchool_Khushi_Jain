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
  entraTenantId: '5aaf8f39-9480-4424-9f90-2efcd26df931'
  entraClientId: '467f0295-3609-488c-a3ff-dd216dc9cf5a'
  entraAudience: '467f0295-3609-488c-a3ff-dd216dc9cf5a'
}

// East US and East US 2 both refuse new SQL servers, so this one sits in Central US.
param sqlSettings = {
  location: 'centralus'
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
