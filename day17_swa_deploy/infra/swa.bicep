param location string = resourceGroup().location
param swaLocation string
param containerAppsEnvironmentName string
param containerRegistryName string
param quotesApiName string
param quotesApiScope string
param bffImage string
param customDomain string = ''
param introspectionEnabled bool = false

var token = uniqueString(resourceGroup().id)
var swaName = 'swa-quotes-${token}'
var acrPull = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: containerAppsEnvironmentName
}

resource quotesApi 'Microsoft.App/containerApps@2024-03-01' existing = {
  name: quotesApiName
}

resource bffIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-quotes-bff'
  location: location
}

resource bffAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, bffIdentity.id, acrPull)
  properties: {
    principalId: bffIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPull)
  }
}

resource bff 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'quotes-bff'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${bffIdentity.id}': {}
    }
  }
  properties: {
    environmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: bffIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'main'
          image: bffImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'QUOTES_API_BASE_URL'
              value: 'https://${quotesApi.properties.configuration.ingress.fqdn}'
            }
            {
              name: 'QUOTES_API_SCOPE'
              value: quotesApiScope
            }
            {
              name: 'TOKEN_INTROSPECTION_ENABLED'
              value: introspectionEnabled ? 'true' : 'false'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: bffIdentity.properties.clientId
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
  dependsOn: [
    bffAcrPull
  ]
}

resource swa 'Microsoft.Web/staticSites@2023-12-01' = {
  name: swaName
  location: swaLocation
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    provider: 'Custom'
    allowConfigFileUpdates: true
    stagingEnvironmentPolicy: 'Enabled'
  }
}

resource backend 'Microsoft.Web/staticSites/linkedBackends@2023-12-01' = {
  parent: swa
  name: 'quotes-bff'
  properties: {
    backendResourceId: bff.id
    region: location
  }
}

resource domain 'Microsoft.Web/staticSites/customDomains@2023-12-01' = if (customDomain != '') {
  parent: swa
  name: customDomain
  properties: {
    validationMethod: 'cname-delegation'
  }
}

output swaName string = swa.name
output swaDefaultHostname string = swa.properties.defaultHostname
output bffFqdn string = bff.properties.configuration.ingress.fqdn
output bffIdentityClientId string = bffIdentity.properties.clientId
output bffIdentityPrincipalId string = bffIdentity.properties.principalId
output quotesApiBaseUrl string = 'https://${quotesApi.properties.configuration.ingress.fqdn}'
