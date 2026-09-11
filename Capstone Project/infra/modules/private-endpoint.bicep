@description('Location for the endpoint')
param location string

@description('Name of the private endpoint')
param name string

@description('Subnet the endpoint takes its private address from')
param subnetId string

@description('Virtual network the private DNS zone is linked to')
param virtualNetworkId string

@description('Resource the endpoint fronts')
param targetResourceId string

@description('Sub-resource being reached, such as sqlServer or vault')
param groupId string

@description('Private DNS zone that makes the public host name resolve to the private address')
param privateDnsZoneName string

resource endpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: name
  location: location
  properties: {
    subnet: {
      id: subnetId
    }
    privateLinkServiceConnections: [
      {
        name: name
        properties: {
          privateLinkServiceId: targetResourceId
          groupIds: [groupId]
        }
      }
    ]
  }
}

// Global, so it is not given a location.
resource zone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: privateDnsZoneName
  location: 'global'
}

// Without this link the zone exists but nothing in the network ever consults it.
resource link 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: zone
  name: '${name}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: virtualNetworkId
    }
  }
}

// What writes the A record: the endpoint's address is not known until it exists.
resource zoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: endpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: replace(privateDnsZoneName, '.', '-')
        properties: {
          privateDnsZoneId: zone.id
        }
      }
    ]
  }
}

output endpointId string = endpoint.id
output privateDnsZoneId string = zone.id
