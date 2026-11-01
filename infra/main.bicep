targetScope = 'subscription'

import { deploymentTags } from './types.bicep'

@description('Required. The one NEW application resource group; never an existing application group.')
@minLength(1)
@maxLength(90)
param applicationResourceGroupName string

@description('Required. The approved Azure public-cloud region. No alternate region is selected by this template.')
@minLength(1)
param location string

@description('Required. A distinct NEW service-managed resource group name. ACA creates and owns it.')
@minLength(1)
@maxLength(90)
param managedResourceGroupName string

@description('Required. Name of the new external workload-profiles Container Apps environment.')
@minLength(1)
param containerAppsEnvironmentName string

@description('Required. Name of the new customer VNet.')
@minLength(1)
param virtualNetworkName string

@description('Required. Approved, canonical IPv4 VNet CIDR containing both nonoverlapping subnets. No network default is provided.')
@minLength(1)
param virtualNetworkAddressPrefix string

@description('Required. Name of the exclusive ACA infrastructure subnet.')
@minLength(1)
param acaSubnetName string

@description('Required. Approved canonical IPv4 /23 for the delegated, exclusive ACA subnet.')
@minLength(1)
param acaSubnetAddressPrefix string

@description('Required. Name of the separate private endpoint subnet.')
@minLength(1)
param privateEndpointSubnetName string

@description('Required. Approved canonical IPv4 /27 for exactly the Table and Blob private endpoints.')
@minLength(1)
param privateEndpointSubnetAddressPrefix string

@description('Required. Name of the Table private endpoint.')
@minLength(1)
param tablePrivateEndpointName string

@description('Required. Name of the Blob private endpoint.')
@minLength(1)
param blobPrivateEndpointName string

@description('Required. VNet-link child name, used once in each of the two fixed private DNS zones.')
@minLength(1)
param privateDnsVnetLinkName string

@description('Required. Name of the new Basic classic-RBAC container registry.')
@minLength(5)
@maxLength(50)
param containerRegistryName string

@description('Required. Name of the new private StorageV2 Standard_LRS account.')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('Required. Name of the new Log Analytics workspace.')
@minLength(1)
param logAnalyticsWorkspaceName string

@description('Required. Name of the sole environment diagnostic setting.')
@minLength(1)
param environmentDiagnosticSettingName string

@description('Required. Web user-assigned managed identity name.')
@minLength(1)
param webIdentityName string

@description('Required. MCP user-assigned managed identity name.')
@minLength(1)
param mcpIdentityName string

@description('Required. GitHub build user-assigned managed identity name.')
@minLength(1)
param githubBuildIdentityName string

@description('Required. GitHub release user-assigned managed identity name.')
@minLength(1)
param githubReleaseIdentityName string

@description('Required. Name of the build UAMI GitHub environment federation.')
@minLength(1)
param githubBuildFederatedCredentialName string

@description('Required. Name of the release UAMI GitHub environment federation.')
@minLength(1)
param githubReleaseFederatedCredentialName string

@description('Required. Display name of the one approved custom Container App update role.')
@minLength(1)
param releaseRoleName string

@description('Required PRIVATE INPUT. The approved stable custom-role GUID. Never generate a replacement or copy it into examples.')
@minLength(1)
param releaseRoleDefinitionGuid string

@description('Required PRIVATE INPUT. Subscription containing the one EXISTING approved Foundry account.')
@minLength(1)
param existingFoundrySubscriptionId string

@description('Required PRIVATE INPUT. Existing resource group containing that Foundry account.')
@minLength(1)
param existingFoundryResourceGroupName string

@description('Required PRIVATE INPUT. Existing Foundry account name. No account/project/model/network change is made.')
@minLength(2)
param existingFoundryAccountName string

@description('Required. Operational labels without credentials, UPNs, or private evidence.')
param tags deploymentTags

module applicationResourceGroupModule 'br/public:avm/res/resources/resource-group:0.4.4' = {
  name: 'application-resource-group'
  params: {
    name: applicationResourceGroupName
    location: environment().name == 'AzureCloud'
      ? location
      : fail('Only the approved Azure public-cloud authority is supported.')
    tags: tags
    enableTelemetry: false
  }
}

// No Container Apps, probe application, Graph objects, or image deployment here.
module foundation './modules/foundation.bicep' = {
  name: 'application-foundation'
  scope: resourceGroup(applicationResourceGroupName)
  params: {
    location: location
    managedResourceGroupName: toLower(managedResourceGroupName) != toLower(applicationResourceGroupName)
      ? managedResourceGroupName
      : fail('The application and service-managed resource group names must differ.')
    containerAppsEnvironmentName: containerAppsEnvironmentName
    virtualNetworkName: virtualNetworkName
    virtualNetworkAddressPrefix: virtualNetworkAddressPrefix
    acaSubnetName: acaSubnetName
    acaSubnetAddressPrefix: acaSubnetAddressPrefix
    privateEndpointSubnetName: privateEndpointSubnetName
    privateEndpointSubnetAddressPrefix: privateEndpointSubnetAddressPrefix
    tablePrivateEndpointName: tablePrivateEndpointName
    blobPrivateEndpointName: blobPrivateEndpointName
    privateDnsVnetLinkName: privateDnsVnetLinkName
    containerRegistryName: containerRegistryName
    storageAccountName: storageAccountName
    logAnalyticsWorkspaceName: logAnalyticsWorkspaceName
    environmentDiagnosticSettingName: environmentDiagnosticSettingName
    webIdentityName: webIdentityName
    mcpIdentityName: mcpIdentityName
    githubBuildIdentityName: githubBuildIdentityName
    githubReleaseIdentityName: githubReleaseIdentityName
    githubBuildFederatedCredentialName: githubBuildFederatedCredentialName
    githubReleaseFederatedCredentialName: githubReleaseFederatedCredentialName
    tags: tags
  }
  dependsOn: [
    applicationResourceGroupModule
  ]
}

// No AVM resource module for this exact custom role definition. Creating it at
// subscription scope avoids a separate bootstrap entry point; its only assignable
// scope is the new RG. CI must not deploy this privileged bootstrap template.
// Schema: https://learn.microsoft.com/azure/templates/microsoft.authorization/2022-04-01/roledefinitions
resource releaseRoleDefinition 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: releaseRoleDefinitionGuid
  properties: {
    roleName: releaseRoleName
    description: 'Read and update the two approved Container Apps and read their revisions. Assigned separately at each app.'
    type: 'CustomRole'
    assignableScopes: [
      applicationResourceGroupModule.outputs.resourceId
    ]
    permissions: [
      {
        actions: [
          'Microsoft.App/containerApps/read'
          'Microsoft.App/containerApps/write'
          'Microsoft.App/containerApps/revisions/read'
        ]
        notActions: []
        dataActions: []
        notDataActions: []
      }
    ]
  }
}

module existingFoundryAccessModule './modules/foundry-access.bicep' = {
  name: 'existing-foundry-web-access'
  scope: resourceGroup(existingFoundrySubscriptionId, existingFoundryResourceGroupName)
  params: {
    existingFoundryAccountName: existingFoundryAccountName
    webPrincipalId: foundation.outputs.webIdentity.principalId
  }
}

@description('Deployment-scope contract. Values are non-secret but environment-private; do not publish deployment outputs.')
output applicationResourceGroup object = {
  name: applicationResourceGroupModule.outputs.name
  resourceId: applicationResourceGroupModule.outputs.resourceId
  location: applicationResourceGroupModule.outputs.location
}

@description('ACA environment and its service-owned group. No platform LB or IP is hand-created.')
output containerAppsEnvironment object = foundation.outputs.containerAppsEnvironment

@description('New VNet, subnets, two private endpoints, and two private DNS zone IDs.')
output network object = foundation.outputs.network

@description('Private state contract, including the URI of the as-yet-uncreated key-ring blob.')
output storage object = foundation.outputs.storage

@description('The new registry ID, name, and login server; never credentials.')
output containerRegistry object = foundation.outputs.containerRegistry

@description('Use clientId for token acquisition and principalId for grants/federation subjects.')
output identities object = {
  web: foundation.outputs.webIdentity
  mcp: foundation.outputs.mcpIdentity
  githubBuild: foundation.outputs.githubBuildIdentity
  githubRelease: foundation.outputs.githubReleaseIdentity
}

@description('Workspace ID and name only; no workspace key is retrieved.')
output logAnalyticsWorkspace object = foundation.outputs.logAnalyticsWorkspace

@description('Bootstrap role contract for workloads.bicep. The stable GUID remains an environment-private value.')
output releaseRole object = {
  name: releaseRoleName
  definitionGuid: releaseRoleDefinitionGuid
  resourceId: releaseRoleDefinition.id
}

@description('The only write to an existing Azure resource: one Web OpenAI User assignment on the approved account.')
output existingFoundryAccess object = {
  accountResourceId: existingFoundryAccessModule.outputs.accountResourceId
  roleAssignmentResourceId: existingFoundryAccessModule.outputs.roleAssignmentResourceId
}
