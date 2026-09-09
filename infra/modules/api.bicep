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

@description('Application Insights connection string the app exports telemetry to')
param applicationInsightsConnectionString string

@description('URI of the Key Vault the app reads its secrets from at startup')
param keyVaultUri string

@description('Host of the Service Bus namespace the app publishes to; empty leaves the broker unconfigured')
param serviceBusFullyQualifiedNamespace string = ''

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

// Derived from the mount path so the database file cannot drift from the volume it sits on.
var sqliteConnectionString = 'Data Source=${api.dataVolumeMountPath}/quotes.db'

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
    secrets: {
      secureList: [
      ]
    }
    // The SQLite file lives on this share, so the database survives a revision replacing the container.
    volumes: [
      {
        name: api.dataVolumeStorageName
        storageName: api.dataVolumeStorageName
        storageType: 'AzureFile'
      }
    ]
    containers: [
      {
        image: fetchLatestImage.outputs.?containers[?0].?image ?? api.bootstrapImage
        name: 'main'
        resources: {
          cpu: json(api.cpu)
          memory: api.memory
        }
        volumeMounts: [
          {
            volumeName: api.dataVolumeStorageName
            mountPath: api.dataVolumeMountPath
          }
        ]
        env: concat(
          [
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: applicationInsightsConnectionString
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
          [
            {
              name: 'ConnectionStrings__DefaultConnection'
              value: sqliteConnectionString
            }
          ]
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
