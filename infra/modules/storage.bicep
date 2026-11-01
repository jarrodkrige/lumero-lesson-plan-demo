targetScope = 'resourceGroup'

import { deploymentTags } from '../types.bicep'

@description('Required. Name of the new, approved storage account.')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('Required. Approved Azure region.')
@minLength(1)
param location string

@description('Required. Operational labels without credentials or private evidence.')
param tags deploymentTags

@description('Required. Web managed identity principal/object ID for data-role assignments.')
@minLength(1)
param webPrincipalId string

@description('Required. MCP managed identity principal/object ID for its Table-only data assignment.')
@minLength(1)
param mcpPrincipalId string

// Bounded AVM exception: storage-account:0.33.1 unconditionally calls listKeys()
// for key/connection-string outputs. Even secure outputs cross the no-key boundary.
// Keep the account direct; published AVM child modules below need no keys.
// Schema: https://learn.microsoft.com/azure/templates/microsoft.storage/2025-06-01/storageaccounts
resource storageAccount 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    isHnsEnabled: false
    isLocalUserEnabled: false
    isSftpEnabled: false
    isNfsV3Enabled: false
    allowCrossTenantReplication: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowBlobPublicAccess: false
    publicNetworkAccess: 'Disabled'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
      resourceAccessRules: []
    }
    encryption: {
      keySource: 'Microsoft.Storage'
      services: {
        blob: {
          enabled: true
          keyType: 'Account'
        }
        // Table key scope is immutable after account creation. This is NOT CMK.
        table: {
          enabled: true
          keyType: 'Account'
        }
      }
    }
  }
}

module tableService 'br/public:avm/res/storage/storage-account/table-service:0.2.0' = {
  name: 'state-table'
  params: {
    storageAccountName: storageAccount.name
    enableTelemetry: false
    tables: [
      {
        name: 'lessonplanner'
        roleAssignments: [
          for principalId in [webPrincipalId, mcpPrincipalId]: {
            // Public built-in Storage Table Data Contributor; scope is this Table.
            roleDefinitionIdOrName: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
            principalId: principalId
            principalType: 'ServicePrincipal'
          }
        ]
      }
    ]
  }
}

module blobService 'br/public:avm/res/storage/storage-account/blob-service:0.2.0' = {
  name: 'key-container'
  params: {
    storageAccountName: storageAccount.name
    enableTelemetry: false
    // Do not silently add retention, versioning, change feed, or backup features.
    automaticSnapshotPolicyEnabled: false
    changeFeedEnabled: false
    containerDeleteRetentionPolicyEnabled: false
    deleteRetentionPolicyEnabled: false
    isVersioningEnabled: false
    lastAccessTimeTrackingPolicyEnabled: false
    restorePolicyEnabled: false
    containers: [
      {
        name: 'dataprotection'
        publicAccess: 'None'
        roleAssignments: [
          {
            // Public built-in Storage Blob Data Contributor; Web only, container scope.
            roleDefinitionIdOrName: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
            principalId: webPrincipalId
            principalType: 'ServicePrincipal'
          }
        ]
      }
    ]
  }
}

@description('Non-secret storage contract. Normal service hostnames must resolve through the two private DNS zones. No blob payload is deployed.')
output configuration object = {
  name: storageAccount.name
  resourceId: storageAccount.id
  tableName: 'lessonplanner'
  tableResourceId: '${tableService.outputs.resourceId}/tables/lessonplanner'
  tableServiceUri: storageAccount.properties.primaryEndpoints.table
  blobServiceUri: storageAccount.properties.primaryEndpoints.blob
  dataProtectionContainerName: 'dataprotection'
  dataProtectionContainerResourceId: '${blobService.outputs.resourceId}/containers/dataprotection'
  dataProtectionBlobUri: '${storageAccount.properties.primaryEndpoints.blob}dataprotection/lumero-keys.xml'
}
