// Per-environment knobs. Every property is optional, so a parameter file sets
// only what differs and a new setting is added here and nowhere else.
type apiSettings = {
  @description('Port the container listens on')
  targetPort: int?

  @description('Lower bound of the scale range; zero lets an idle environment scale to nothing')
  minReplicas: int?

  @description('Upper bound of the scale range')
  maxReplicas: int?

  @description('CPU cores per replica, as a string because Bicep has no decimal literal')
  cpu: string?

  @description('Memory per replica')
  memory: string?

  @description('ASP.NET Core environment name the container runs under')
  aspNetCoreEnvironment: string?

  @description('Image used before one of the app\'s own has been pushed to the registry')
  bootstrapImage: string?
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

@description('Environment overrides; anything omitted falls back to the defaults below')
param settings apiSettings = {}

var defaults = {
  targetPort: 8080
  minReplicas: 1
  maxReplicas: 10
  cpu: '0.5'
  memory: '1.0Gi'
  aspNetCoreEnvironment: 'Production'
  bootstrapImage: 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
}

var api = union(defaults, settings)

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
    containers: [
      {
        image: fetchLatestImage.outputs.?containers[?0].?image ?? api.bootstrapImage
        name: 'main'
        resources: {
          cpu: json(api.cpu)
          memory: api.memory
        }
        env: [
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
          // Only a pointer, so no secret value sits in the container's environment.
          {
            name: 'KeyVault__Uri'
            value: keyVaultUri
          }
        ]
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
