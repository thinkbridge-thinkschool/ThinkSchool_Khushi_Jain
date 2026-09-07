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

@description('Vault the app\'s connection string is written to')
param keyVaultName string

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

resource appAuthorizationRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2022-10-01-preview' = {
  parent: namespace
  name: 'quotes-api'
  properties: {
    rights: [
      'Send'
      'Listen'
    ]
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

// The .NET Key Vault provider maps the double dash to ':', so this arrives as ServiceBus:ConnectionString.
resource connectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'ServiceBus--ConnectionString'
  properties: {
    value: appAuthorizationRule.listKeys().primaryConnectionString
  }
}

output namespaceName string = namespace.name
output topicName string = topic.name
