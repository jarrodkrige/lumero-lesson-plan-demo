// Compile-only, deliberately nondeployable example. The proxy gate is NOT proved.
// Foundation outputs and Graph bootstrap supply distinct private IDs, not credentials.
using './workloads.bicep'

param applicationResourceGroupName = '<application-rg>'
param location = '<approved-region>'
param containerAppsEnvironmentName = '<aca-environment>'
param containerRegistryName = '<registry>'
param storageAccountName = '<storage-account>'
param webAppName = '<web-app>'
param mcpAppName = '<mcp-app>'
param webImage = 'registry.invalid/lumero-web@sha256:<required-immutable-digest>'
param mcpImage = 'registry.invalid/lumero-mcp@sha256:<required-immutable-digest>'
param webIdentityResourceId = '<web-uami-resource-id>'
param webIdentityClientId = '<web-uami-client-id>'
param mcpIdentityResourceId = '<mcp-uami-resource-id>'
param mcpIdentityClientId = '<mcp-uami-client-id>'
param githubReleaseIdentityResourceId = '<release-uami-resource-id>'
param githubReleasePrincipalId = '<release-uami-principal-object-id>'
param releaseRoleDefinitionGuid = '<private-stable-role-guid>'
param tenantId = '<private-tenant-id>'
param presenterAppClientId = '<presenter-oidc-app-client-id>'
param mcpApiClientId = '<separate-mcp-api-client-id>'
param presenterObjectIds = ['<approved-presenter-object-id>']
param webCanonicalFqdn = 'web.invalid'
param mcpCanonicalFqdn = 'mcp.internal.invalid'
param verifiedProxyCidr = '<BLOCKED-until-authoritative-narrow-proxy-proof>'
param foundryEndpoint = 'https://existing-foundry.invalid/openai/v1/'
param tags = {
  owner: '<non-pii-owner-label>'
  workshop: '<approved-workshop-label>'
  'approved-expiry': '<approved-expiry-utc>'
  'source-revision': '<validated-source-commit>'
}
