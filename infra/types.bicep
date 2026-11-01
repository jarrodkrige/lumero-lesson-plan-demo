@export()
@sealed()
@description('Required operational labels. Use a non-PII owner label; never put credentials or private evidence in tags.')
type deploymentTags = {
  owner: string
  workshop: string
  'approved-expiry': string
  'source-revision': string
}

@export()
@sealed()
@description('A managed identity client ID selects credentials; its principal ID is used for Azure/Graph grants.')
type identityOutput = {
  name: string
  resourceId: string
  clientId: string
  principalId: string
}
