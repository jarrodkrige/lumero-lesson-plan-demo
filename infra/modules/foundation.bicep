targetScope = 'resourceGroup'

import { deploymentTags, identityOutput } from '../types.bicep'

@description('Required. Approved Azure region.')
param location string

@description('Required. Distinct new ACA service-managed resource group name.')
param managedResourceGroupName string

@description('Required. New external workload-profiles ACA environment name.')
param containerAppsEnvironmentName string

@description('Required. New VNet name.')
param virtualNetworkName string

@description('Required. Approved canonical IPv4 VNet CIDR.')
param virtualNetworkAddressPrefix string

@description('Required. Exclusive ACA subnet name.')
param acaSubnetName string

@description('Required. Approved canonical IPv4 /23 ACA subnet CIDR.')
param acaSubnetAddressPrefix string

@description('Required. Private endpoint subnet name.')
param privateEndpointSubnetName string

@description('Required. Approved canonical IPv4 /27 private endpoint subnet CIDR.')
param privateEndpointSubnetAddressPrefix string

@description('Required. Table private endpoint name.')
param tablePrivateEndpointName string

@description('Required. Blob private endpoint name.')
param blobPrivateEndpointName string

@description('Required. Link name used in each private DNS zone.')
param privateDnsVnetLinkName string

@description('Required. New Basic classic-RBAC registry name.')
param containerRegistryName string

@description('Required. New private storage account name.')
param storageAccountName string

@description('Required. New Log Analytics workspace name.')
param logAnalyticsWorkspaceName string

@description('Required. Environment diagnostic setting name.')
param environmentDiagnosticSettingName string

@description('Required. Web UAMI name.')
param webIdentityName string

@description('Required. MCP UAMI name.')
param mcpIdentityName string

@description('Required. GitHub build UAMI name.')
param githubBuildIdentityName string

@description('Required. GitHub release UAMI name.')
param githubReleaseIdentityName string

@description('Required. Build federation child name.')
param githubBuildFederatedCredentialName string

@description('Required. Release federation child name.')
param githubReleaseFederatedCredentialName string

@description('Required. Non-secret operational labels.')
param tags deploymentTags

var checkedVnetPrefix = !contains(virtualNetworkAddressPrefix, ':') && parseCidr(virtualNetworkAddressPrefix).network == first(split(
    virtualNetworkAddressPrefix,
    '/'
  ))
  ? virtualNetworkAddressPrefix
  : fail('Supply the approved canonical IPv4 VNet CIDR.')
var checkedAcaPrefix = endsWith(acaSubnetAddressPrefix, '/23') && !contains(acaSubnetAddressPrefix, ':') && parseCidr(acaSubnetAddressPrefix).network == first(split(
    acaSubnetAddressPrefix,
    '/'
  ))
  ? acaSubnetAddressPrefix
  : fail('The exclusive ACA subnet must be the approved canonical IPv4 /23.')
var checkedEndpointPrefix = endsWith(privateEndpointSubnetAddressPrefix, '/27') && !contains(
    privateEndpointSubnetAddressPrefix,
    ':'
  ) && parseCidr(privateEndpointSubnetAddressPrefix).network == first(split(privateEndpointSubnetAddressPrefix, '/'))
  ? privateEndpointSubnetAddressPrefix
  : fail('The private endpoint subnet must be the approved canonical IPv4 /27.')

// Distinct identities are a security boundary, not merely a naming convention.
var identityNames = [webIdentityName, mcpIdentityName, githubBuildIdentityName, githubReleaseIdentityName]
var checkedIdentityNames = length(union(map(identityNames, name => toLower(name)), [])) == 4 ? identityNames : fail('Supply four distinct managed identity names; workload, build, and release identities cannot be merged.')
var checkedAcaSubnetName = toLower(acaSubnetName) != toLower(privateEndpointSubnetName)
  ? acaSubnetName
  : fail('The exclusive ACA subnet and private endpoint subnet must have distinct names.')
var checkedTableEndpointName = toLower(tablePrivateEndpointName) != toLower(blobPrivateEndpointName)
  ? tablePrivateEndpointName
  : fail('Table and Blob require two distinct private endpoints.')

module virtualNetwork 'br/public:avm/res/network/virtual-network:0.10.2' = {
  name: 'application-network'
  params: {
    name: virtualNetworkName
    location: location
    tags: tags
    enableTelemetry: false
    addressPrefixes: [checkedVnetPrefix]
    // Azure-provided DNS; no peering, NSG, routes, NAT, or service endpoints.
    dnsServers: []
    subnets: [
      {
        name: checkedAcaSubnetName
        addressPrefix: checkedAcaPrefix
        delegation: 'Microsoft.App/environments'
        privateEndpointNetworkPolicies: 'Enabled'
      }
      {
        name: privateEndpointSubnetName
        addressPrefix: checkedEndpointPrefix
        privateEndpointNetworkPolicies: 'Disabled'
      }
    ]
  }
}

module webIdentityModule 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  name: 'web-identity'
  params: {
    name: checkedIdentityNames[0]
    location: location
    tags: tags
    enableTelemetry: false
    // Presenter-app/Web-UAMI federation is Graph bootstrap, not a UAMI child.
  }
}

module mcpIdentityModule 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  name: 'mcp-identity'
  params: {
    name: checkedIdentityNames[1]
    location: location
    tags: tags
    enableTelemetry: false
  }
}

module githubBuildIdentityModule 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  name: 'github-build-identity'
  params: {
    name: checkedIdentityNames[2]
    location: location
    tags: tags
    enableTelemetry: false
    federatedIdentityCredentials: [
      {
        name: githubBuildFederatedCredentialName
        issuer: 'https://token.actions.githubusercontent.com'
        subject: 'repo:jarrodkrige@10612771/lumero-lesson-plan-demo@1441161999:environment:lumero-build'
        audiences: ['api://AzureADTokenExchange']
      }
    ]
  }
}

module githubReleaseIdentityModule 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  name: 'github-release-identity'
  params: {
    name: checkedIdentityNames[3]
    location: location
    tags: tags
    enableTelemetry: false
    federatedIdentityCredentials: [
      {
        name: githubReleaseFederatedCredentialName
        issuer: 'https://token.actions.githubusercontent.com'
        subject: 'repo:jarrodkrige@10612771/lumero-lesson-plan-demo@1441161999:environment:lumero-release'
        audiences: ['api://AzureADTokenExchange']
      }
    ]
  }
}

module containerRegistryModule 'br/public:avm/res/container-registry/registry:0.13.1' = {
  name: 'application-registry'
  params: {
    name: containerRegistryName
    location: location
    tags: tags
    enableTelemetry: false
    acrSku: 'Basic'
    roleAssignmentMode: 'LegacyRegistryPermissions'
    acrAdminUserEnabled: false
    anonymousPullEnabled: false
    azureADAuthenticationAsArmPolicyStatus: 'enabled'
    publicNetworkAccess: 'Enabled'
    // Basic has no firewall/private endpoint. Avoid AVM's Premium-only deny-rule default.
    networkRuleSetDefaultAction: 'Allow'
    networkRuleBypassOptions: 'None'
    networkRuleBypassAllowedForTasks: false
    dataEndpointEnabled: false
    softDeletePolicyStatus: 'disabled'
    roleAssignments: [
      {
        roleDefinitionIdOrName: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
        principalId: webIdentityModule.outputs.principalId
        principalType: 'ServicePrincipal'
      }
      {
        roleDefinitionIdOrName: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
        principalId: mcpIdentityModule.outputs.principalId
        principalType: 'ServicePrincipal'
      }
      {
        roleDefinitionIdOrName: '8311e382-0749-4cb8-b61a-304f252e45ec'
        principalId: githubBuildIdentityModule.outputs.principalId
        principalType: 'ServicePrincipal'
      }
    ]
  }
}

module privateStorage './storage.bicep' = {
  name: 'application-storage'
  params: {
    storageAccountName: storageAccountName
    location: location
    tags: tags
    webPrincipalId: webIdentityModule.outputs.principalId
    mcpPrincipalId: mcpIdentityModule.outputs.principalId
  }
}

var storageEndpoints = [
  { service: 'table', name: checkedTableEndpointName }
  { service: 'blob', name: blobPrivateEndpointName }
]

module privateDnsZones 'br/public:avm/res/network/private-dns-zone:0.8.1' = [
  for endpoint in storageEndpoints: {
    name: '${endpoint.service}-private-dns'
    params: {
      name: 'privatelink.${endpoint.service}.${environment().suffixes.storage}'
      location: 'global'
      tags: tags
      enableTelemetry: false
      virtualNetworkLinks: [
        {
          name: privateDnsVnetLinkName
          virtualNetworkResourceId: virtualNetwork.outputs.resourceId
          registrationEnabled: false
          resolutionPolicy: 'Default'
          tags: tags
        }
      ]
    }
  }
]

module privateEndpoints 'br/public:avm/res/network/private-endpoint:0.12.1' = [
  for (endpoint, index) in storageEndpoints: {
    name: '${endpoint.service}-private-endpoint'
    params: {
      name: endpoint.name
      location: location
      tags: tags
      enableTelemetry: false
      subnetResourceId: virtualNetwork.outputs.subnetResourceIds[1]
      privateLinkServiceConnections: [
        {
          name: endpoint.name
          properties: {
            privateLinkServiceId: privateStorage.outputs.configuration.resourceId
            groupIds: [endpoint.service]
          }
        }
      ]
      privateDnsZoneGroup: {
        name: 'default'
        privateDnsZoneGroupConfigs: [
          {
            name: endpoint.service
            privateDnsZoneResourceId: privateDnsZones[index].outputs.resourceId
          }
        ]
      }
    }
  }
]

// Bounded AVM exception: workspace:0.16.1 unconditionally retrieves shared keys
// as outputs. This resource configures Azure Monitor with local auth disabled,
// never calls listKeys(), and has no CMK, solutions, exports, or extra services.
// Schema: https://learn.microsoft.com/azure/templates/microsoft.operationalinsights/2025-07-01/workspaces
resource logAnalyticsWorkspaceResource 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: {
      // Bicep has no decimal literal; same representation as AVM's decimal-cap input.
      // Service acceptance/effective cap are unproved. Never round up on failure.
      dailyQuotaGb: json('0.1')
    }
    features: {
      disableLocalAuth: true
      enableLogAccessUsingOnlyResourcePermissions: false
    }
    forceCmkForQuery: false
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

module containerAppsEnvironmentModule 'br/public:avm/res/app/managed-environment:0.16.0' = {
  name: 'application-environment'
  params: {
    name: containerAppsEnvironmentName
    location: location
    tags: tags
    enableTelemetry: false
    internal: false
    publicNetworkAccess: 'Enabled'
    zoneRedundant: false
    peerTrafficEncryption: false
    infrastructureSubnetResourceId: virtualNetwork.outputs.subnetResourceIds[0]
    infrastructureResourceGroupName: managedResourceGroupName
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    // Do NOT supply logAnalyticsWorkspaceResourceId here: that path retrieves keys.
    appLogsConfiguration: { destination: 'azure-monitor' }
    // Leave ingressConfiguration absent: default ingress, not Premium ingress.
    diagnosticSettings: [
      {
        name: environmentDiagnosticSettingName
        workspaceResourceId: logAnalyticsWorkspaceResource.id
        metricCategories: []
        logCategoriesAndGroups: [
          { category: 'ContainerAppConsoleLogs', enabled: true }
          { category: 'ContainerAppSystemLogs', enabled: true }
        ]
      }
    ]
  }
}

@description('Web credential-selection and grant identifiers are intentionally separate.')
output webIdentity identityOutput = {
  name: webIdentityModule.outputs.name
  resourceId: webIdentityModule.outputs.resourceId
  clientId: webIdentityModule.outputs.clientId
  principalId: webIdentityModule.outputs.principalId
}

@description('MCP identity has only registry-pull and Table-data access.')
output mcpIdentity identityOutput = {
  name: mcpIdentityModule.outputs.name
  resourceId: mcpIdentityModule.outputs.resourceId
  clientId: mcpIdentityModule.outputs.clientId
  principalId: mcpIdentityModule.outputs.principalId
}

@description('Build identity is distinct from the release identity.')
output githubBuildIdentity identityOutput = {
  name: githubBuildIdentityModule.outputs.name
  resourceId: githubBuildIdentityModule.outputs.resourceId
  clientId: githubBuildIdentityModule.outputs.clientId
  principalId: githubBuildIdentityModule.outputs.principalId
}

@description('Release receives its two app-scoped grants only during workloads bootstrap.')
output githubReleaseIdentity identityOutput = {
  name: githubReleaseIdentityModule.outputs.name
  resourceId: githubReleaseIdentityModule.outputs.resourceId
  clientId: githubReleaseIdentityModule.outputs.clientId
  principalId: githubReleaseIdentityModule.outputs.principalId
}

@description('No service-owned public IP or load balancer is managed by this template.')
output containerAppsEnvironment object = {
  name: containerAppsEnvironmentModule.outputs.name
  resourceId: containerAppsEnvironmentModule.outputs.resourceId
  defaultDomain: containerAppsEnvironmentModule.outputs.defaultDomain
  managedResourceGroupName: managedResourceGroupName
  managedResourceGroupResourceId: subscriptionResourceId('Microsoft.Resources/resourceGroups', managedResourceGroupName)
}

@description('Only the approved customer network and Table/Blob private connectivity.')
output network object = {
  virtualNetworkName: virtualNetwork.outputs.name
  virtualNetworkResourceId: virtualNetwork.outputs.resourceId
  acaSubnetResourceId: virtualNetwork.outputs.subnetResourceIds[0]
  privateEndpointSubnetResourceId: virtualNetwork.outputs.subnetResourceIds[1]
  tablePrivateEndpointResourceId: privateEndpoints[0].outputs.resourceId
  blobPrivateEndpointResourceId: privateEndpoints[1].outputs.resourceId
  tablePrivateDnsZoneResourceId: privateDnsZones[0].outputs.resourceId
  blobPrivateDnsZoneResourceId: privateDnsZones[1].outputs.resourceId
}

@description('State URIs contain no credentials or query strings.')
output storage object = privateStorage.outputs.configuration

@description('Registry outputs contain no administrative credentials.')
output containerRegistry object = {
  name: containerRegistryModule.outputs.name
  resourceId: containerRegistryModule.outputs.resourceId
  loginServer: containerRegistryModule.outputs.loginServer
}

@description('Azure Monitor workspace reference without keys.')
output logAnalyticsWorkspace object = {
  name: logAnalyticsWorkspaceResource.name
  resourceId: logAnalyticsWorkspaceResource.id
}
