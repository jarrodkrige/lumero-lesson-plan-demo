targetScope = 'resourceGroup'

import { deploymentTags } from './types.bicep'

@description('Required. The application RG created by main.bicep. The current deployment scope must match.')
@minLength(1)
param applicationResourceGroupName string

@description('Required. The approved region from the foundation output.')
@minLength(1)
param location string

@description('Required. EXISTING foundation environment name in the application RG.')
@minLength(1)
param containerAppsEnvironmentName string

@description('Required. EXISTING foundation registry name in the application RG.')
@minLength(5)
@maxLength(50)
param containerRegistryName string

@description('Required. EXISTING foundation storage account name in the application RG.')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('Required. Name of the external Web Container App.')
@minLength(1)
param webAppName string

@description('Required. Name of the internal MCP Container App.')
@minLength(1)
param mcpAppName string

@description('Required. Immutable foundation-login-server/lumero-web@sha256:<64 lowercase hex characters>. Other repositories, nested paths and tags are rejected.')
@minLength(1)
@maxLength(512)
param webImage string

@description('Required. Immutable foundation-login-server/lumero-mcp@sha256:<64 lowercase hex characters>. Other repositories, nested paths and tags are rejected.')
@minLength(1)
@maxLength(512)
param mcpImage string

@description('Required. Web UAMI resource ID from main identities.web.resourceId, not its principal/client ID.')
@minLength(1)
param webIdentityResourceId string

@description('Required. Web UAMI CLIENT ID from main identities.web.clientId, used for all Web token acquisition and MCP azp.')
@minLength(1)
param webIdentityClientId string

@description('Required. MCP UAMI resource ID from main identities.mcp.resourceId.')
@minLength(1)
param mcpIdentityResourceId string

@description('Required. MCP UAMI CLIENT ID from main identities.mcp.clientId; it selects MCP Table credentials only.')
@minLength(1)
param mcpIdentityClientId string

@description('Required. Release UAMI resource ID from main identities.githubRelease.resourceId.')
@minLength(1)
param githubReleaseIdentityResourceId string

@description('Required. Release UAMI PRINCIPAL/object ID from main identities.githubRelease.principalId, not its client ID.')
@minLength(1)
param githubReleasePrincipalId string

@description('Required PRIVATE INPUT. Stable GUID from main releaseRole.definitionGuid, not a built-in role GUID.')
@minLength(1)
param releaseRoleDefinitionGuid string

@description('Required PRIVATE INPUT. Single approved public-cloud Entra tenant ID shared by the identities and app registrations.')
@minLength(1)
param tenantId string

@description('Required PRIVATE INPUT. Presenter confidential OIDC APPLICATION client ID; not the Web UAMI or MCP API client ID.')
@minLength(1)
param presenterAppClientId string

@description('Required PRIVATE INPUT. Separate MCP API APPLICATION client-ID GUID: v2 aud is this GUID, scope is api://GUID/.default.')
@minLength(1)
param mcpApiClientId string

@description('Required PRIVATE INPUT. One to twenty approved presenter object IDs. No names, UPNs, or implicit deployment-identity allowlisting.')
@minLength(1)
@maxLength(20)
param presenterObjectIds string[]

@description('Required. Exact canonical Web FQDN, without scheme/path. Must equal webAppName plus the actual environment defaultDomain.')
@minLength(1)
param webCanonicalFqdn string

@description('Required. Exact MCP FQDN, without scheme/path. Must include the internal segment of the actual environment hostname.')
@minLength(1)
param mcpCanonicalFqdn string

@description('Required PRIVATE INPUT. The approved EXISTING account HTTPS endpoint ending exactly /openai/v1/. No project URL, credentials, query, or fallback.')
@minLength(1)
param foundryEndpoint string

@description('Required. Operational labels without credentials, UPNs, or private evidence.')
param tags deploymentTags

resource managedEnvironment 'Microsoft.App/managedEnvironments@2026-01-01' existing = {
  name: toLower(resourceGroup().name) == toLower(applicationResourceGroupName)
    ? containerAppsEnvironmentName
    : fail('Deploy workloads only into the foundation application resource group.')
}

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: containerRegistryName
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-06-01' existing = {
  name: storageAccountName
}

resource webIdentityExisting 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: last(split(webIdentityResourceId, '/'))
}

resource mcpIdentityExisting 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: last(split(mcpIdentityResourceId, '/'))
}

resource releaseIdentityExisting 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: last(split(githubReleaseIdentityResourceId, '/'))
}

resource releaseRoleExisting 'Microsoft.Authorization/roleDefinitions@2022-04-01' existing = {
  name: releaseRoleDefinitionGuid
  scope: subscription()
}

var checkedWebClientId = toLower(webIdentityExisting.id) == toLower(webIdentityResourceId) && toLower(webIdentityExisting.properties.clientId) == toLower(webIdentityClientId) && toLower(webIdentityExisting.properties.tenantId) == toLower(tenantId) && toLower(webIdentityClientId) != toLower(mcpIdentityClientId) && toLower(webIdentityClientId) != toLower(presenterAppClientId)
  ? webIdentityClientId
  : fail('Use the distinct foundation Web identity resource/client IDs in the approved tenant.')
var checkedMcpClientId = toLower(mcpIdentityExisting.id) == toLower(mcpIdentityResourceId) && toLower(mcpIdentityExisting.properties.clientId) == toLower(mcpIdentityClientId) && toLower(mcpIdentityExisting.properties.tenantId) == toLower(tenantId) && toLower(mcpIdentityClientId) != toLower(presenterAppClientId)
  ? mcpIdentityClientId
  : fail('Use the foundation MCP identity resource/client IDs in the approved tenant.')
var checkedReleasePrincipalId = toLower(releaseIdentityExisting.id) == toLower(githubReleaseIdentityResourceId) && toLower(releaseIdentityExisting.properties.principalId) == toLower(githubReleasePrincipalId) && toLower(releaseIdentityExisting.properties.tenantId) == toLower(tenantId) && toLower(githubReleaseIdentityResourceId) != toLower(webIdentityResourceId) && toLower(githubReleaseIdentityResourceId) != toLower(mcpIdentityResourceId)
  ? githubReleasePrincipalId
  : fail('Use the foundation release identity principal/object ID, not a client ID.')
var checkedMcpApiClientId = toLower(mcpApiClientId) != toLower(presenterAppClientId) && toLower(mcpApiClientId) != toLower(webIdentityClientId) && toLower(mcpApiClientId) != toLower(mcpIdentityClientId)
  ? mcpApiClientId
  : fail('Presenter OIDC, Web managed identity, and MCP API audiences must be separate.')

var releaseActions = [
  'Microsoft.App/containerApps/read'
  'Microsoft.App/containerApps/write'
  'Microsoft.App/containerApps/revisions/read'
]
var rolePermissions = releaseRoleExisting.properties.permissions ?? []
var permission = rolePermissions[?0] ?? {}
var assignableScopes = releaseRoleExisting.properties.assignableScopes ?? []
var checkedReleaseRoleId = releaseRoleExisting.properties.type == 'CustomRole' && length(assignableScopes) == 1 && contains(
    map(assignableScopes, scope => toLower(scope)),
    toLower(resourceGroup().id)
  ) && length(rolePermissions) == 1 && length(permission.?actions ?? []) == 3 && length(intersection(
    permission.?actions ?? [],
    releaseActions
  )) == 3 && empty(permission.?notActions ?? []) && empty(permission.?dataActions ?? []) && empty(permission.?notDataActions ?? [])
  ? releaseRoleExisting.id
  : fail('Release must use the exact bootstrap custom role with only the new RG assignable scope and no data actions.')

var checkedWebFqdn = webCanonicalFqdn == '${webAppName}.${managedEnvironment.properties.defaultDomain}'
  ? webCanonicalFqdn
  : fail('Web AllowedHosts and QR origin must use the exact canonical foundation-environment hostname.')
var checkedMcpFqdn = mcpCanonicalFqdn == '${mcpAppName}.internal.${managedEnvironment.properties.defaultDomain}'
  ? mcpCanonicalFqdn
  : fail('MCP must use its exact internal foundation-environment hostname.')

var foundryHost = split(foundryEndpoint, '/')[?2] ?? ''
var checkedFoundryEndpoint = startsWith(foundryEndpoint, 'https://') && endsWith(foundryEndpoint, '/openai/v1/') && length(split(
    foundryEndpoint,
    '/'
  )) == 6 && !contains(foundryHost, '@') && !contains(foundryHost, ':') && !contains(foundryEndpoint, '?') && !contains(
    foundryEndpoint,
    '#'
  ) && (endsWith(foundryHost, '.openai.azure.com') || endsWith(foundryHost, '.services.ai.azure.com') || endsWith(
    foundryHost,
    '.cognitiveservices.azure.com'
  ))
  ? foundryEndpoint
  : fail('Use the approved existing-account Azure OpenAI v1 HTTPS endpoint without credentials or extra URI components.')

var imageRepositories = [
  'lumero-web'
  'lumero-mcp'
]
var checkedImages = map(
  [webImage, mcpImage],
  (image, applicationIndex) =>
    first(split(image, '@sha256:')) == '${registry.properties.loginServer}/${imageRepositories[applicationIndex]}' && length(split(
        image,
        '@sha256:'
      )) == 2 && length(last(split(image, '@sha256:'))) == 64 && !empty(last(split(first(split(image, '@sha256:')), '/'))) && !contains(
        last(split(first(split(image, '@sha256:')), '/')),
        ':'
      ) && empty(filter(
        range(0, length(last(split(image, '@sha256:')))),
        index => !contains('0123456789abcdef', substring(last(split(image, '@sha256:')), index, 1))
      ))
      ? image
      : fail('Web requires lumero-web and MCP requires lumero-mcp in the foundation registry, each with a lowercase SHA-256 digest and no tag.')
)

var commonEnvironment = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
  { name: 'Logging__LogLevel__Default', value: 'Warning' }
  { name: 'Logging__LogLevel__Microsoft.AspNetCore', value: 'Warning' }
  { name: 'Logging__LogLevel__Azure', value: 'Warning' }
  { name: 'Logging__LogLevel__ModelContextProtocol', value: 'Warning' }
  { name: 'Storage__TableServiceUri', value: storageAccount.properties.primaryEndpoints.table }
  { name: 'Storage__TableName', value: 'lessonplanner' }
  { name: 'Quiz__PublicBaseUrl', value: 'https://${checkedWebFqdn}' }
  { name: 'Quiz__LifetimeHours', value: '24' }
  { name: 'Quiz__ApprovalMinutes', value: '5' }
  { name: 'Quiz__MaximumAttempts', value: '200' }
]

var presenterEnvironment = [
  for (objectId, index) in presenterObjectIds: { name: 'Presenter__AllowedObjectIds__${index}', value: objectId }
]

var webEnvironment = concat(
  commonEnvironment,
  [
    { name: 'AllowedHosts', value: checkedWebFqdn }
    { name: 'Logging__LogLevel__Microsoft.Identity.Web', value: 'Warning' }
    { name: 'Logging__LogLevel__System.Net.Http.HttpClient', value: 'Warning' }
    { name: 'Logging__LogLevel__Microsoft.Agents.AI', value: 'Warning' }
    { name: 'AzureAd__Instance', value: environment().authentication.loginEndpoint }
    { name: 'AzureAd__TenantId', value: tenantId }
    { name: 'AzureAd__ClientId', value: presenterAppClientId }
    { name: 'AzureAd__CallbackPath', value: '/signin-oidc' }
    { name: 'AzureAd__ClientCredentials__0__SourceType', value: 'SignedAssertionFromManagedIdentity' }
    { name: 'AzureAd__ClientCredentials__0__ManagedIdentityClientId', value: checkedWebClientId }
    { name: 'AzureAd__ClientCredentials__0__TokenExchangeUrl', value: 'api://AzureADTokenExchange/.default' }
    { name: 'Mcp__Endpoint', value: 'https://${checkedMcpFqdn}/mcp' }
    { name: 'Mcp__TokenScope', value: 'api://${checkedMcpApiClientId}/.default' }
    { name: 'Mcp__CallerClientId', value: checkedWebClientId }
    { name: 'Mcp__ManagedIdentityClientId', value: checkedWebClientId }
    { name: 'Storage__ManagedIdentityClientId', value: checkedWebClientId }
    {
      name: 'DataProtection__BlobUri'
      value: '${storageAccount.properties.primaryEndpoints.blob}dataprotection/lumero-keys.xml'
    }
    { name: 'DataProtection__ManagedIdentityClientId', value: checkedWebClientId }
    { name: 'ReverseProxy__UseFixedHttpsOrigin', value: 'true' }
    { name: 'Foundry__Endpoint', value: checkedFoundryEndpoint }
    { name: 'Foundry__Deployment', value: 'gpt-6.1-terra' }
    { name: 'Foundry__ManagedIdentityClientId', value: checkedWebClientId }
  ],
  presenterEnvironment
)

var mcpEnvironment = concat(commonEnvironment, [
  { name: 'AllowedHosts', value: checkedMcpFqdn }
  { name: 'Mcp__TenantId', value: tenantId }
  { name: 'Mcp__Audience', value: checkedMcpApiClientId }
  { name: 'Mcp__CallerClientId', value: checkedWebClientId }
  { name: 'Mcp__RequiredRole', value: 'Quiz.Publish' }
  { name: 'Storage__ManagedIdentityClientId', value: checkedMcpClientId }
])

// Fixed cardinality and sizing. There is no app-count, SKU, auth, replica, or ingress-security override.
var checkedWebAppName = toLower(webAppName) != toLower(mcpAppName)
  ? webAppName
  : fail('Web and MCP must remain two distinct Container Apps.')

var applications = [
  {
    role: 'web'
    name: checkedWebAppName
    external: true
    cpu: 1
    memory: '2Gi'
    identityResourceId: webIdentityResourceId
  }
  {
    role: 'mcp'
    name: mcpAppName
    external: false
    cpu: json('0.25')
    memory: '0.5Gi'
    identityResourceId: mcpIdentityResourceId
  }
]

var applicationEnvironments = [webEnvironment, mcpEnvironment]
var probeHosts = [checkedWebFqdn, checkedMcpFqdn]

module containerApps 'br/public:avm/res/app/container-app:0.23.0' = [
  for (app, index) in applications: {
    name: '${app.role}-workload'
    params: {
      name: app.name
      location: environment().name == 'AzureCloud'
        ? location
        : fail('Only the approved Azure public-cloud authority is supported.')
      tags: tags
      enableTelemetry: false
      environmentResourceId: managedEnvironment.id
      workloadProfileName: 'Consumption'
      managedIdentities: {
        systemAssigned: false
        userAssignedResourceIds: [app.identityResourceId]
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: app.identityResourceId
        }
      ]
      activeRevisionsMode: 'Single'
      disableIngress: false
      ingressExternal: app.external
      ingressAllowInsecure: false
      ingressTransport: 'auto'
      ingressTargetPort: 8080
      traffic: [{ latestRevision: true, weight: 100 }]
      scaleSettings: {
        minReplicas: 1
        maxReplicas: 1
        rules: []
      }
      containers: [
        {
          name: app.role
          image: checkedImages[index]
          env: applicationEnvironments[index]
          resources: { cpu: app.cpu, memory: app.memory }
          probes: [
            {
              type: 'Startup'
              tcpSocket: { port: 8080 }
              initialDelaySeconds: 10
              periodSeconds: 5
              timeoutSeconds: 3
              failureThreshold: 24
              successThreshold: 1
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
                scheme: 'HTTP'
                httpHeaders: [{ name: 'Host', value: probeHosts[index] }]
              }
              periodSeconds: 10
              timeoutSeconds: 3
              failureThreshold: 3
              successThreshold: 1
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
                scheme: 'HTTP'
                httpHeaders: [{ name: 'Host', value: probeHosts[index] }]
              }
              periodSeconds: 10
              timeoutSeconds: 6
              failureThreshold: 3
              successThreshold: 1
            }
          ]
        }
      ]
      // Exactly one assignment per app, to the distinct release identity.
      roleAssignments: [
        {
          roleDefinitionIdOrName: checkedReleaseRoleId
          principalId: checkedReleasePrincipalId
          principalType: 'ServicePrincipal'
        }
      ]
    }
  }
]

@description('Actual Web deployment outputs. Do not publish these environment-private values.')
output web object = {
  name: containerApps[0].outputs.name
  resourceId: containerApps[0].outputs.resourceId
  fqdn: containerApps[0].outputs.fqdn
  publicBaseUrl: 'https://${containerApps[0].outputs.fqdn}'
  image: webImage
}

@description('Actual MCP output; internal ingress is environment-scoped, not a substitute for API authentication.')
output mcp object = {
  name: containerApps[1].outputs.name
  resourceId: containerApps[1].outputs.resourceId
  fqdn: containerApps[1].outputs.fqdn
  endpoint: 'https://${containerApps[1].outputs.fqdn}/mcp'
  image: mcpImage
}

@description('The two app-scoped release grants, using the same deterministic naming as the pinned AVM module.')
output releaseRoleAssignmentResourceIds array = [
  for index in range(0, 2): extensionResourceId(
    containerApps[index].outputs.resourceId,
    'Microsoft.Authorization/roleAssignments',
    guid(containerApps[index].outputs.resourceId, checkedReleasePrincipalId, checkedReleaseRoleId)
  )
]
