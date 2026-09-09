type apiSettings = {
  targetPort: int?
  minReplicas: int?
  maxReplicas: int?
  cpu: string?
  memory: string?
  aspNetCoreEnvironment: string?
  bootstrapImage: string?
  dataVolumeStorageName: string?
  dataVolumeMountPath: string?
  entraTenantId: string?
  entraClientId: string?
  entraAudience: string?
}

@description('The location used for all deployed resources')
param location string

@description('Tags that will be applied to all resources')
param tags object = {}

@description('Name of the container app, and the azd service name it is tagged with')
param name string = 'quotes-api'

@description('True when the container app already exists, so its current image is reused')
param exists bool

@description('Resource id of the Container Apps environment that hosts the app')
param environmentResourceId string

@description('Login server of the registry the image is pulled from')
param containerRegistryLoginServer string

@description('Resource id of the user-assigned identity the app runs as')
param identityResourceId string

@description('Client id of that identity, which the Azure SDK credential chain reads')
param identityClientId string

@description('Key Vault URI of the Application Insights connection string the app exports telemetry to')
param applicationInsightsSecretUri string

@description('URI of the Key Vault the app reads its secrets from at startup')
param keyVaultUri string

@description('Host of the Service Bus namespace the app publishes to; empty leaves the broker unconfigured')
param serviceBusFullyQualifiedNamespace string = ''

@description('Fully qualified name of the SQL server the app reads and writes; empty leaves it on SQLite')
param sqlFullyQualifiedDomainName string = ''

@description('Database on that server')
param sqlDatabaseName string = ''

@description('Environment overrides; anything omitted falls back to the defaults below')
param settings apiSettings = {}

var defaults = {
  targetPort: 8080
  minReplicas: 1
  maxReplicas: 10
  cpu: '0.5'
  memory: '1Gi'
  aspNetCoreEnvironment: 'Production'
  bootstrapImage: 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
  dataVolumeStorageName: 'quotesdata'
  dataVolumeMountPath: '/data'
  entraTenantId: ''
  entraClientId: ''
  entraAudience: ''
}

var api = union(defaults, settings)

var useSqlServer = !empty(sqlFullyQualifiedDomainName)

var applicationInsightsSecretName = 'appinsights-connection-string'

// Derived from the mount path so the database file cannot drift from the volume it sits on.
var sqliteConnectionString = 'Data Source=${api.dataVolumeMountPath}/quotes.db'

// No password field: the client fetches an Entra token for the identity User Id names.
var sqlConnectionString = join(
  [
    'Server=tcp:${sqlFullyQualifiedDomainName},1433'
    'Database=${sqlDatabaseName}'
    'Encrypt=True'
    'TrustServerCertificate=False'
    'Authentication=Active Directory Default'
    'User Id=${identityClientId}'
  ],
  ';'
)

var databaseEnv = useSqlServer
  ? [
      {
        name: 'Database__Provider'
        value: 'SqlServer'
      }
      {
        name: 'ConnectionStrings__DefaultConnection'
        value: sqlConnectionString
      }
    ]
  : [
      {
        name: 'ConnectionStrings__DefaultConnection'
        value: sqliteConnectionString
      }
    ]

// The share carries the SQLite file across revisions, and has nothing to hold once the app is on SQL Server.
var dataVolumes = useSqlServer
  ? []
  : [
      {
        name: api.dataVolumeStorageName
        storageName: api.dataVolumeStorageName
        storageType: 'AzureFile'
      }
    ]

var dataVolumeMounts = useSqlServer
  ? []
  : [
      {
        volumeName: api.dataVolumeStorageName
        mountPath: api.dataVolumeMountPath
      }
    ]

var entraEnv = empty(api.entraTenantId)
  ? []
  : [
      {
        name: 'Entra__TenantId'
        value: api.entraTenantId
      }
      {
        name: 'Entra__Audience'
        value: api.entraAudience
      }
      {
        name: 'Entra__ClientId'
        value: api.entraClientId
      }
    ]

// A host name, not a credential: the app authenticates to it with its managed identity.
var serviceBusEnv = empty(serviceBusFullyQualifiedNamespace)
  ? []
  : [
      {
        name: 'ServiceBus__FullyQualifiedNamespace'
        value: serviceBusFullyQualifiedNamespace
      }
    ]

module fetchLatestImage './fetch-container-image.bicep' = {
  name: '${name}-fetch-image'
  params: {
    exists: exists
    name: name
  }
}

module app 'br/public:avm/res/app/container-app:0.8.0' = {
  name: '${name}-container-app'
  params: {
    name: name
    ingressTargetPort: api.targetPort
    scaleMinReplicas: api.minReplicas
    scaleMaxReplicas: api.maxReplicas
    // The platform reads the vault with the app's identity, so the value is a pointer here and never a literal.
    secrets: {
      secureList: [
        {
          name: applicationInsightsSecretName
          keyVaultUrl: applicationInsightsSecretUri
          identity: identityResourceId
        }
      ]
    }
    volumes: dataVolumes
    containers: [
      {
        image: fetchLatestImage.outputs.?containers[?0].?image ?? api.bootstrapImage
        name: 'main'
        resources: {
          cpu: json(api.cpu)
          memory: api.memory
        }
        volumeMounts: dataVolumeMounts
        env: concat(
          [
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              secretRef: applicationInsightsSecretName
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: identityClientId
            }
            {
              name: 'PORT'
              value: string(api.targetPort)
            }
            // Without this the app falls back to Development and loads developer settings in production.
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: api.aspNetCoreEnvironment
            }
            {
              name: 'KeyVault__Uri'
              value: keyVaultUri
            }
          ],
          entraEnv,
          serviceBusEnv,
          databaseEnv
        )
      }
    ]
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [identityResourceId]
    }
    registries: [
      {
        server: containerRegistryLoginServer
        identity: identityResourceId
      }
    ]
    environmentResourceId: environmentResourceId
    location: location
    tags: union(tags, { 'azd-service-name': name })
  }
}

output resourceId string = app.outputs.resourceId
