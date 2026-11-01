targetScope = 'resourceGroup'

@description('Required. The single EXISTING approved Foundry account in this module deployment scope.')
@minLength(2)
param existingFoundryAccountName string

@description('Required. Web UAMI principal/object ID, NOT its client ID or the presenter application client ID.')
@minLength(1)
param webPrincipalId string

// A resource module for a Cognitive Services account would manage that existing account.
// This bounded direct extension resource changes only its one approved role assignment.
// Schema: https://learn.microsoft.com/azure/templates/microsoft.authorization/2022-04-01/roleassignments
resource foundryAccount 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: existingFoundryAccountName
}

// Public built-in role identifier: Cognitive Services OpenAI User.
var openAiUserRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
)

resource webOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryAccount.id, webPrincipalId, openAiUserRoleId)
  scope: foundryAccount
  properties: {
    roleDefinitionId: openAiUserRoleId
    principalId: webPrincipalId
    principalType: 'ServicePrincipal'
  }
}

@description('The existing account, not a new or modified Foundry resource.')
output accountResourceId string = foundryAccount.id

@description('The only write made by this module.')
output roleAssignmentResourceId string = webOpenAiUser.id
