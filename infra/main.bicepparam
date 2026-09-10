using './main.bicep'

// azd loads .azure/<env>/.env before building this, so the environment name it
// set selects which settings file applies. Matching on a prefix keeps a
// region-suffixed name like 'production' on the prod settings.
var settings = startsWith(readEnvironmentVariable('AZURE_ENV_NAME'), 'prod')
  ? loadJsonContent('./environments/prod.json')
  : loadJsonContent('./environments/dev.json')

param environmentName = readEnvironmentVariable('AZURE_ENV_NAME')
param location = readEnvironmentVariable('AZURE_LOCATION')
param quotesApiExists = readEnvironmentVariable('SERVICE_QUOTES_API_RESOURCE_EXISTS', 'false') == 'true'

param principalId = readEnvironmentVariable('AZURE_PRINCIPAL_ID')
param principalType = readEnvironmentVariable('AZURE_PRINCIPAL_TYPE', 'User')
param jwtSigningKey = readEnvironmentVariable('AZURE_JWT_SIGNING_KEY')

param deployDataServices = settings.deployDataServices
param deployContainerApp = settings.?deployContainerApp ?? true
param apiSettings = settings.apiSettings
param sqlSettings = settings.sqlSettings
param serviceBusSettings = settings.serviceBusSettings
param alertSettings = settings.alertSettings
param alertEmail = readEnvironmentVariable('AZURE_ALERT_EMAIL', '')
