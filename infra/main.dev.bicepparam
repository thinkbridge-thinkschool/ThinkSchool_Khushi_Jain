using './main.bicep'

var settings = loadJsonContent('./environments/dev.json')

param environmentName = 'dev'
param location = 'eastus'
param quotesApiExists = true

param principalId = readEnvironmentVariable('AZURE_PRINCIPAL_ID')
param principalType = readEnvironmentVariable('AZURE_PRINCIPAL_TYPE', 'User')
param jwtSigningKey = readEnvironmentVariable('AZURE_JWT_SIGNING_KEY')

param deployDataServices = settings.deployDataServices
param apiSettings = settings.apiSettings
param sqlSettings = settings.sqlSettings
param serviceBusSettings = settings.serviceBusSettings
param alertSettings = settings.alertSettings
param alertEmail = readEnvironmentVariable('AZURE_ALERT_EMAIL', '')
