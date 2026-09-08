using './main.bicep'

var settings = loadJsonContent('./environments/prod.json')

param environmentName = 'prod'
param location = 'eastus'
param quotesApiExists = false

param principalId = readEnvironmentVariable('AZURE_PRINCIPAL_ID')
param principalType = readEnvironmentVariable('AZURE_PRINCIPAL_TYPE', 'User')
param jwtSigningKey = readEnvironmentVariable('AZURE_JWT_SIGNING_KEY')

param deployDataServices = settings.deployDataServices
param apiSettings = settings.apiSettings
param sqlSettings = settings.sqlSettings
param serviceBusSettings = settings.serviceBusSettings
