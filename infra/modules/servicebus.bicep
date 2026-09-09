type serviceBusSettings = {
  sku: string?
  capacity: int?
  maxDeliveryCount: int?
  messageTimeToLive: string?
  topicName: string?
  auditSubscriptionName: string?
  moderationSubscriptionName: string?
}

@description('The location used for all deployed resources')
param location string

@description('Tags that will be applied to all resources')
param tags object = {}

@description('Name of the Service Bus namespace')
param name string

@description('Principal id of the identity the API runs as, granted send and receive on the namespace')
param appPrincipalId string

@description('Environment overrides; anything omitted falls back to the defaults below')
param settings serviceBusSettings = {}

var defaults = {
  sku: 'Standard'
  capacity: 1
  maxDeliveryCount: 5
  messageTimeToLive: 'P14D'
  topicName: 'quote-events'
  auditSubscriptionName: 'audit'
  moderationSubscriptionName: 'moderation'
}

var bus = union(defaults, settings)

// Capacity is Premium-only and is rejected on a lower tier.
var skuBlock = bus.sku == 'Premium'
  ? { name: bus.sku, tier: bus.sku, capacity: bus.capacity }
  : { name: bus.sku, tier: bus.sku }

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: name
  location: location
  tags: tags
  sku: skuBlock
  properties: {
    minimumTlsVersion: '1.2'
  }
}

resource topic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: namespace
  name: bus.topicName
  properties: {
    defaultMessageTimeToLive: bus.messageTimeToLive
  }
}

resource auditSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topic
  name: bus.auditSubscriptionName
  properties: {
    maxDeliveryCount: bus.maxDeliveryCount
    deadLetteringOnMessageExpiration: true
  }
}

resource moderationSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topic
  name: bus.moderationSubscriptionName
  properties: {
    maxDeliveryCount: bus.maxDeliveryCount
    deadLetteringOnMessageExpiration: true
  }
}

var appRoleIds = [
  '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39' // Azure Service Bus Data Sender
  '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0' // Azure Service Bus Data Receiver
]

// Access by role rather than by key, so the namespace issues nothing that has to be held or rotated.
resource appRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleId in appRoleIds: {
    scope: namespace
    name: guid(namespace.id, appPrincipalId, roleId)
    properties: {
      principalId: appPrincipalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    }
  }
]

output namespaceName string = namespace.name
output topicName string = topic.name

// serviceBusEndpoint carries a scheme and a port, which the SDK's namespace argument does not take.
output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
