# Lumero production infrastructure

Standalone Bicep for the existing application, not an `azd` scaffold. This directory prepares the fixed production footprint; **it does not prove production readiness or authorise cloud execution**. All entry-point parameters are required. SKUs, resource counts, security settings, health routes and application contracts are not configuration switches.

`main.example.bicepparam` and `workloads.example.bicepparam` are compile-only, deliberately **nondeployable** contracts. Replace every placeholder only in protected execution inputs, never in these public files. Names, addresses, tenant/subscription/client/principal IDs, presenter IDs, endpoints and actual deployment outputs are environment-private even when they are not credentials. Never put them, tokens, private evidence or UPNs into public artefacts, build contexts, images or logs. `infra/.local/` and `*.local.bicepparam` / `*.local.parameters.json` are ignored conveniences, not a secret store. No connection string, account key, client secret or workspace key is accepted.

## Entry-point order and ownership

1. **Privileged foundation bootstrap: `main.bicep`, subscription scope.** Creates the new application resource group, foundation, one custom release role and seven Azure role assignments. ACA itself creates the separately named service-managed resource group. No Container App or probe application is deployed. The existing Foundry account receives only the Web OpenAI User assignment; no account, project, deployment or networking setting is changed. The role definition is created here, so no separate role-definition entry point is needed.
2. **Complete the production prerequisites.** Complete external Graph bootstrap, protected image-digest build, exact canonical-host configuration and execution approval. The fixed-origin variant requires HTTPS-only ingress, with `allowInsecure=false` verified through ARM before production authentication. It consumes no forwarded headers and requires no proxy CIDR. Foundation availability does not prove application authentication or inference.
3. **Privileged workload bootstrap: `workloads.bicep`, application resource-group scope.** References the existing foundation; creates only the two configured apps and two app-scoped release assignments. Requires immutable images and distinct identity/application inputs. Foundation and custom-role outputs must already exist. Account for RBAC propagation without adding broader grants or secret fallbacks.
4. **Routine release is not bootstrap.** The GitHub release identity updates the two existing apps using its approved actions. It must not redeploy either privileged entry point. `containerApps/write` is not field-level “image-only” authorisation: the release workflow must constrain its updates. Compatibility of the chosen update client with the exact role remains an execution-owner gate; missing permissions do not justify silently broadening the role.

These steps describe order, not commands to execute. New resource-group names must actually be unused; ARM resource declarations alone cannot guarantee this. Bootstrap requires pre-existing authority at the relevant subscription/new RG and the one existing Foundry account. Nothing here signs in or grants the deployer additional permissions.

## Fixed footprint

| Component | Required configuration |
| --- | --- |
| Network | One customer VNet; exclusive delegated IPv4 `/23` ACA subnet (`Microsoft.App/environments`); separate IPv4 `/27` private-endpoint subnet. Canonical, nonoverlapping, contained address ranges are approved private inputs, not defaults. No customer NAT, NSG, UDR or peering. |
| ACA environment | External workload-profiles environment; only the `Consumption` profile; default ingress, not Premium; no dedicated profile or zone redundancy. Its Standard load balancer and two public IPs are service-owned consequences, not hand-created resources. |
| Web | External HTTPS-only ingress, target 8080, 1 CPU / 2Gi, `Single` revision mode, min=max=1. |
| MCP | Internal environment-scoped HTTPS-only ingress, target 8080, 0.25 CPU / 0.5Gi, `Single` revision mode, min=max=1. Internal ingress does not replace API authentication. |
| Registry | ACR Basic, classic RBAC (`LegacyRegistryPermissions`), admin and anonymous pull off, managed-identity image pull; ARM-audience authentication enabled for that pull. Public registry endpoint, no Premium firewall or registry private endpoint. |
| Storage | StorageV2 / Standard_LRS / Hot / flat namespace; Microsoft-managed account-scoped Table encryption at account creation; Blob account encryption. Shared keys and public blobs off; public network off; HTTPS/TLS1.2; ACL Deny and bypass None. No SFTP/NFS/local users or cross-tenant replication. |
| State | One Table `lessonplanner`; one private Blob container `dataprotection`. No retention/versioning/backup feature is added. IaC does **not** create the `lumero-keys.xml` blob payload; Web's DataProtection integration creates it at runtime. |
| Private connectivity | Exactly Table and Blob private endpoints, their two `privatelink` DNS zones and one VNet link per zone, with registration off. Applications use normal storage HTTPS hostnames resolved privately. No other endpoint. |
| Logs | Log Analytics PerGB2018, 30-day retention, proposed `0.1` GB/day cap, local authentication off. ACA logging destination `azure-monitor`; one diagnostic setting containing only `ContainerAppConsoleLogs` and `ContainerAppSystemLogs`, no metrics and no workspace keys. No Azure Monitor private-link topology. |
| Identities | Four distinct UAMIs: Web, MCP, GitHub build, GitHub release. No system-assigned identity or credential fallback. |

Min=max=1 and single active revision do **not** exclude rollout overlap, restarts or transient billing. There is no App Insights, Key Vault, Functions/DTS, SQL, hosted Foundry, extra application, or hand-managed platform load balancer/public IP. Module deployment records and selected child resources are not extra application services; AVM telemetry is explicitly disabled.

### Azure grants and federation

Exactly **nine Azure assignments** across the two phases:

| Principal | Role | Exact scope | Phase |
| --- | --- | --- | --- |
| Web, MCP | AcrPull (one each) | New registry | Foundation |
| GitHub build | AcrPush | New registry | Foundation |
| Web, MCP | Storage Table Data Contributor (one each) | Single `lessonplanner` Table | Foundation |
| Web only | Storage Blob Data Contributor | `dataprotection` container | Foundation |
| Web only | Cognitive Services OpenAI User | One parameterised existing Foundry account | Foundation |
| GitHub release | Approved custom role (one each) | Each of the two apps | Workloads |

The one custom role has only `Microsoft.App/containerApps/read`, `Microsoft.App/containerApps/write`, and `Microsoft.App/containerApps/revisions/read`; no `notActions`, `dataActions` or `notDataActions`. Its sole assignable scope is the new application RG. Its stable GUID is a required private input, not generated or copied into this repository. Workloads check the existing role's permissions and assignable scope before using it.

Two ARM UAMI federations have issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and these exact subjects:

- `repo:jarrodkrige@10612771/lumero-lesson-plan-demo@1441161999:environment:lumero-build`
- `repo:jarrodkrige@10612771/lumero-lesson-plan-demo@1441161999:environment:lumero-release`

These are this public repository's immutable GitHub owner/repository IDs, not Azure identifiers. [GitHub's immutable subject format](https://docs.github.com/en/actions/reference/security/oidc#immutable-subject-claims) applies to repositories created after 15 July 2026 and to opted-in repositories. Read `GET /repos/{owner}/{repo}/actions/oidc/customization/sub` before bootstrap and match its `sub_claim_prefix`; do not infer the old plain-name format or disable immutable subjects. The runner guard and both ARM credentials must agree. A fork needs its own exact binding and corresponding reviewed source changes. A subject-contract correction requires reconciliation, focused validation and a new protected workflow run; do not rerun a failed delivery or print token claims.

The third trust is **external Graph bootstrap**, not another UAMI ARM federation: the Presenter confidential application trusts the Web UAMI **principal/object ID**. Its assertion issuer is the approved tenant's Entra v2 issuer and its federation audience is `api://AzureADTokenExchange`. Microsoft.Identity.Web selects the Web UAMI by **client ID** and requests `api://AzureADTokenExchange/.default` for the assertion. Graph also owns the separate MCP API application, v2 access-token setting, `Quiz.Publish` application role and the Web service principal's app-role assignment. IaC creates none of these Graph objects or grants.

## Required parameter contracts

Both entry points require `tags` with exactly `owner`, `workshop`, `approved-expiry`, and `source-revision`. Use non-PII operational labels. The expiry tag is metadata, not a paid-window or automatic cleanup control. If no expiry is approved, use `not-scheduled` rather than inventing a date. No entry-point parameter has a deployment default.

### `main.bicep`: 29 parameters

| Group | Names |
| --- | --- |
| Scope/environment | `applicationResourceGroupName`, `location`, `managedResourceGroupName`, `containerAppsEnvironmentName` |
| Network | `virtualNetworkName`, `virtualNetworkAddressPrefix`, `acaSubnetName`, `acaSubnetAddressPrefix`, `privateEndpointSubnetName`, `privateEndpointSubnetAddressPrefix`, `tablePrivateEndpointName`, `blobPrivateEndpointName`, `privateDnsVnetLinkName` |
| Registry/state/logs | `containerRegistryName`, `storageAccountName`, `logAnalyticsWorkspaceName`, `environmentDiagnosticSettingName` |
| Identity names | `webIdentityName`, `mcpIdentityName`, `githubBuildIdentityName`, `githubReleaseIdentityName` |
| Federation child names | `githubBuildFederatedCredentialName`, `githubReleaseFederatedCredentialName` |
| Custom role | `releaseRoleName`, `releaseRoleDefinitionGuid` |
| Existing account scope | `existingFoundrySubscriptionId`, `existingFoundryResourceGroupName`, `existingFoundryAccountName` |
| Labels | `tags` |

The application and managed RG names must differ. Identity names must be distinct; subnet names and the two private-endpoint names must also differ. No private address or resource naming scheme is supplied by the templates.

### `workloads.bicep`: 24 parameters

| Parameter(s) | Source / meaning |
| --- | --- |
| `applicationResourceGroupName`, `location` | `main.applicationResourceGroup.name`, `.location`; deployment scope must match. |
| `containerAppsEnvironmentName`, `containerRegistryName`, `storageAccountName` | Respective foundation output `.name` values; these resources are only referenced, not recreated. |
| `webAppName`, `mcpAppName` | Distinct approved new application names. |
| `webImage`, `mcpImage` | Web: `<foundation-login-server>/lumero-web@sha256:<64-lowercase-hex>`; MCP: `<foundation-login-server>/lumero-mcp@sha256:<64-lowercase-hex>`. Repository names are fixed. Wrong, swapped or nested repositories, tag-only and tag-plus-digest inputs are rejected. No build or mutable tag resolution happens in IaC. |
| `webIdentityResourceId`, `webIdentityClientId` | `main.identities.web.resourceId`, `.clientId`. |
| `mcpIdentityResourceId`, `mcpIdentityClientId` | `main.identities.mcp.resourceId`, `.clientId`. |
| `githubReleaseIdentityResourceId`, `githubReleasePrincipalId` | `main.identities.githubRelease.resourceId`, `.principalId`, **not** `.clientId`. |
| `releaseRoleDefinitionGuid` | `main.releaseRole.definitionGuid`. |
| `tenantId`, `presenterAppClientId`, `mcpApiClientId`, `presenterObjectIds` | Distinct approved tenant/Graph inputs. Presenter allowlist contains one to twenty object IDs, never UPNs or an implicit deployment identity. |
| `webCanonicalFqdn` | Exact `<webAppName>.<main.containerAppsEnvironment.defaultDomain>`, without scheme/path. |
| `mcpCanonicalFqdn` | Exact `<mcpAppName>.internal.<main.containerAppsEnvironment.defaultDomain>`, without scheme/path. |
| `foundryEndpoint` | Explicit approved existing-account HTTPS `/openai/v1/` URL, not a project URL; no credentials, query or fallback. Must correspond to the exact account granted in foundation. IaC does not infer this from private account metadata. |
| `tags` | Same required operational labels. |

Client/principal inputs are cross-checked against the existing UAMIs and tenant. Hostnames are checked against the actual environment default domain. These checks cannot establish live HTTPS/Host enforcement, that the Foundry endpoint/deployment is operational, or that an input was administratively approved.

### Machine-readable outputs (no credentials)

Consume the named ARM output **values** privately; do not publish or echo actual values. The following is the stable shape, not live deployment output.

- `main.applicationResourceGroup`: `name`, `resourceId`, `location`.
- `main.containerAppsEnvironment`: `name`, `resourceId`, `defaultDomain`, `managedResourceGroupName`, `managedResourceGroupResourceId`.
- `main.network`: `virtualNetworkName`, `virtualNetworkResourceId`, `acaSubnetResourceId`, `privateEndpointSubnetResourceId`, `tablePrivateEndpointResourceId`, `blobPrivateEndpointResourceId`, `tablePrivateDnsZoneResourceId`, `blobPrivateDnsZoneResourceId`.
- `main.storage`: `name`, `resourceId`, `tableName`, `tableResourceId`, `tableServiceUri`, `blobServiceUri`, `dataProtectionContainerName`, `dataProtectionContainerResourceId`, `dataProtectionBlobUri`.
- `main.containerRegistry`: `name`, `resourceId`, `loginServer`.
- `main.identities`: `web`, `mcp`, `githubBuild`, `githubRelease`; each has `name`, `resourceId`, `clientId`, `principalId`.
- `main.logAnalyticsWorkspace`: `name`, `resourceId`.
- `main.releaseRole`: `name`, `definitionGuid`, `resourceId`.
- `main.existingFoundryAccess`: `accountResourceId`, `roleAssignmentResourceId`.
- `workloads.web`: `name`, `resourceId`, `fqdn`, `publicBaseUrl`, `image`.
- `workloads.mcp`: `name`, `resourceId`, `fqdn`, `endpoint`, `image`.
- `workloads.releaseRoleAssignmentResourceIds`: two app-scoped assignment IDs, Web then MCP.

## Production application configuration

Both containers run `Production` with `ASPNETCORE_HTTP_PORTS=8080`, warning-level logging, exact `AllowedHosts`, `Storage__TableName=lessonplanner` and the foundation `Storage__TableServiceUri`. Each uses its **own** `Storage__ManagedIdentityClientId`. Both receive the canonical Web HTTPS `Quiz__PublicBaseUrl`, with quiz lifetime 24 hours, approval window 5 minutes and maximum attempts 200. The public quiz/QR origin is never derived from a caller-supplied Host header.

| Web setting | Value source |
| --- | --- |
| `AzureAd__Instance`, `AzureAd__TenantId`, `AzureAd__ClientId` | Public-cloud `https://login.microsoftonline.com/`, approved tenant, **Presenter application client ID**. Entry points reject non-public Azure clouds. |
| `AzureAd__CallbackPath` | `/signin-oidc` |
| `AzureAd__ClientCredentials__0__SourceType` | `SignedAssertionFromManagedIdentity` |
| `AzureAd__ClientCredentials__0__ManagedIdentityClientId` | Web UAMI **client** ID |
| `AzureAd__ClientCredentials__0__TokenExchangeUrl` | `api://AzureADTokenExchange/.default` |
| `Presenter__AllowedObjectIds__N` | Indexed approved presenter object IDs |
| `Mcp__Endpoint` | `https://<canonical-internal-mcp-fqdn>/mcp` |
| `Mcp__TokenScope` | `api://<mcp-api-client-id>/.default` |
| `Mcp__CallerClientId`, `Mcp__ManagedIdentityClientId` | Web UAMI client ID |
| `DataProtection__BlobUri`, `DataProtection__ManagedIdentityClientId` | Private-resolved Blob service URI plus `dataprotection/lumero-keys.xml`; Web UAMI client ID |
| `ReverseProxy__UseFixedHttpsOrigin` | Fixed `true`; Production-only HTTPS scheme and exact canonical Host |
| `Foundry__Endpoint`, `Foundry__Deployment`, `Foundry__ManagedIdentityClientId` | Explicit existing-account `/openai/v1/` endpoint; fixed `gpt-4.1`; Web UAMI client ID |

MCP receives `Mcp__TenantId`, `Mcp__Audience=<mcp-api-client-ID-GUID>` (**not** the `api://.../.default` scope), `Mcp__CallerClientId=<web-uami-client-id>`, and `Mcp__RequiredRole=Quiz.Publish`. It gets no Presenter OIDC, Blob, Foundry or Web reverse-proxy configuration. OIDC audience, MCP audience, managed-identity selection, and the application's separate Foundry `https://ai.azure.com/.default` token scope remain distinct.

No `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, trust-list clearing, wildcard AllowedHosts or new proxy/probe app is introduced. The production Web boundary fixes the HTTPS scheme before other application middleware and rejects any non-canonical Host, including on probe paths. It does not consume `X-Forwarded-Proto`, `X-Forwarded-For` or `X-Forwarded-Host`, and adds no trusted proxy/network. DataProtection's application name remains `Lumero.LessonPlanner.v1`.

This is a revision-2 deviation from the former proxy-CIDR prerequisite, using [ASP.NET Core's documented fixed-scheme approach when all external requests are secure](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0#when-it-isnt-possible-to-add-forwarded-headers-and-all-requests-are-secure). ACA enforces external HTTPS; the application does not infer origin or participant IP from a header. `ReverseProxy__UseFixedHttpsOrigin` defaults on in Production and has no effect in Development/Testing. Verify the effective ingress, exact OIDC HTTPS redirect, ignored spoofed headers and accepted health probes before live authentication. No inference is needed for those checks.

The anonymous request-rate partition is shared across participants: **600 requests per minute, fixed window, zero queue**. This provides request headroom for sixty active participants submitting within a minute. Existing Web concurrency is **32 requests, zero queue**, so this does not guarantee sixty requests executing simultaneously. Presenter limits, antiforgery, secure cookies, identity audiences and persistence controls are unchanged. Rate/concurrency limits remain per process and reset on replacement or overlap; the durable attempt budget remains server-owned.

### Actual health routes

| Probe | Protocol / target | Initial delay | Period | Timeout | Failures |
| --- | --- | --- | --- | --- | --- |
| Startup | TCP 8080 | 10s | 5s | 3s | 24 |
| Liveness | HTTP 8080 `/health/live` | Platform default | 10s | 3s | 3 |
| Readiness | HTTP 8080 `/health/ready` | Platform default | 10s | 6s | 3 |

All success thresholds are one. Each HTTP probe supplies `Host` equal to that app's exact `AllowedHosts`. The existing application's Table readiness timeout stays **five seconds**, within the six-second HTTP readiness timeout. Compilation cannot prove Linux startup, HTTPS ingress or exact redirects, identity propagation or actual probe acceptance.

Release accepts a selected revision only when it is active, `Provisioned`, `Healthy`, and either `Running` or `RunningAtMaxScale`, with the same writable template and exact image. [Azure documents “Running (at max)” as a running revision with no reported issues](https://learn.microsoft.com/azure/container-apps/revisions#lifecycle). This state is expected with the fixed one-replica maximum and does not authorise a scaling change. Inactive, unhealthy, incompletely provisioned or changed revisions remain unacceptable.

App and revision GET responses represent some platform defaults differently. Revision comparison normalises only an empty/absent revision suffix, absent/null [polling interval (30 seconds) and cooldown period (300 seconds)](https://learn.microsoft.com/azure/container-apps/scale-app#custom), and the [server-only `resources.ephemeralStorage` value](https://learn.microsoft.com/javascript/api/@azure/arm-appcontainers/containerresources?view=azure-node-latest). Image PATCH bodies omit that non-writable storage metadata. CPU, memory, image, environment, probes, scale limits, non-default interval values and every other template field remain compared. The separate non-image app-drift guard is unchanged.

## Pinned modules and bounded direct resources

Research used the [AVM index](https://azure.github.io/Azure-Verified-Modules/indexes/bicep/bicep-resource-modules/), official module examples/source, MCR version tags and Microsoft Learn resource schemas. The Azure-specific MCP guidance/schema helpers were unavailable; no unrelated toolset or replacement deployment service was used. Every registry reference is pinned and telemetry is explicitly disabled.

All module paths below have prefix `br/public:avm/res/`:

| Module | Version |
| --- | --- |
| `resources/resource-group` | `0.4.4` |
| `network/virtual-network` | `0.10.2` |
| `managed-identity/user-assigned-identity` | `0.6.0` |
| `container-registry/registry` | `0.13.1` |
| `network/private-dns-zone` | `0.8.1` |
| `network/private-endpoint` | `0.12.1` |
| `app/managed-environment` | `0.16.0` |
| `storage/storage-account/blob-service` | `0.2.0` |
| `storage/storage-account/table-service` | `0.2.0` |
| `app/container-app` | `0.23.0` |

Pinned source/examples are under `https://github.com/Azure/bicep-registry-modules/tree/avm/res/{module}/{version}/avm/res/{module}`; tag discovery uses `https://mcr.microsoft.com/v2/bicep/avm/res/{module}/tags/list`. Parent Storage and workspace modules were reviewed rather than assumed safe based on their names.

Bounded exceptions:

- **Storage account: direct `2025-06-01`.** Reviewed `storage/storage-account:0.33.1` unconditionally retrieves keys/connection strings as outputs, including secure outputs. Secure annotations do not remove key retrieval. Direct account creation enforces immutable account-scoped Table encryption without changing an unrelated service's key type; published AVM Blob/Table child modules still create state and their narrowly scoped grants. [ARM schema](https://learn.microsoft.com/azure/templates/microsoft.storage/2025-06-01/storageaccounts), [Table encryption guidance](https://learn.microsoft.com/azure/storage/common/account-encryption-key-create).
- **Workspace: direct `2025-07-01`.** Reviewed `operational-insights/workspace:0.16.1` unconditionally retrieves shared keys. This resource never retrieves keys and disables local authentication. The fractional cap uses `json('0.1')`, as Bicep has no decimal literal. Service acceptance remains unproved; failure must not silently round the cap up. [ARM schema](https://learn.microsoft.com/azure/templates/microsoft.operationalinsights/2025-07-01/workspaces).
- **Custom role: direct `2022-04-01`.** No applicable AVM resource module provides this exact subscription-defined, single-RG-assignable custom role. No broad bootstrap role or extra grant is substituted. [Role-definition schema](https://learn.microsoft.com/azure/templates/microsoft.authorization/2022-04-01/roledefinitions).
- **Existing Foundry grant: direct role assignment `2022-04-01`.** An account module would manage the existing account; the small scoped module instead declares the account `existing` at `2025-09-01` and writes one extension assignment only. [Assignment schema](https://learn.microsoft.com/azure/templates/microsoft.authorization/2022-04-01/roleassignments), [account schema](https://learn.microsoft.com/azure/templates/microsoft.cognitiveservices/2025-09-01/accounts).

Selected AVM resources use these API versions: resource group `2025-04-01`; UAMIs/federation `2024-11-30`; VNet/subnets/private endpoints/zone groups `2025-05-01`; private DNS zone `2020-06-01` and VNet links `2024-06-01`; ACR **`2025-06-01-preview`**; Blob service/container `2025-01-01`; Table service/table `2025-06-01`; ACA environment/apps `2026-01-01`; diagnostics `2021-05-01-preview`; scoped role assignments `2022-04-01`. Workloads' read-only existing-registry reference uses `2025-04-01`. Pinned AVM templates also contain inactive optional branches; those are not evidence that additional resources or key-based logging are selected. In particular, the environment receives `destination: azure-monitor` and **no** `appLogsConfiguration.logAnalyticsWorkspaceResourceId`, keeping its key-based logging branch inactive.

Further first-party service references: [ACA VNet requirements](https://learn.microsoft.com/azure/container-apps/custom-virtual-networks), [networking](https://learn.microsoft.com/azure/container-apps/networking), [internal app hostnames](https://learn.microsoft.com/azure/container-apps/connect-apps), [managed-identity image pull](https://learn.microsoft.com/azure/container-apps/managed-identity-image-pull), [health probes](https://learn.microsoft.com/azure/container-apps/health-probes), [Azure Monitor logging](https://learn.microsoft.com/azure/container-apps/log-options), [daily-cap behaviour](https://learn.microsoft.com/azure/azure-monitor/logs/daily-cap), [Bicep CIDR functions](https://learn.microsoft.com/azure/azure-resource-manager/bicep/bicep-functions-cidr).

## Local validation only

From the repository root:

```powershell
python -B infra\validate.py
```

Local compilation and checks were run with **Bicep CLI 0.44.1 (28275db947)**. The standard-library-only checker locates an **already installed** `bicep` (PATH or the existing Azure CLI cache), or accepts `--bicep <installed-executable>`. It never invokes Azure CLI, signs in, installs tools, starts services or reads private parameter files. Bicep may restore the public pinned modules to its normal local module cache; an unavailable compiler/registry route is an error, not an installation or deployment fallback.

Checks run `bicep lint` and verify formatting of authored Bicep/parameter examples, compile both entry points and both invalid examples via `--stdout` into memory, and inspect the selected authored/compiled contracts: required inputs, fixed cardinality, scoped grants, security settings, auth environment, immutable images, fixed HTTPS origin and health probes. No compiled-result files are written. Static checks examine selected module parameters rather than counting every inactive AVM branch; they are not an ARM expression evaluator or Azure deployment validation. `git diff --check -- infra` is an additional whitespace check. If changing Bicep later, format it and **rerun lint**.

Image checks bind the compiled repository order, authored exact-path guard and container image selection, and check the deliberately invalid examples. A bounded in-memory reference model exercises two valid controls plus wrong, swapped, nested, tagged, malformed-digest and foreign-registry inputs. These cases check the image contract, not ARM expression execution or image availability.

### Remaining execution gates

The fixed-origin variant requires **verified HTTPS-only ingress and the exact canonical Host** before production authentication. No proxy CIDR is consumed or needed. Read `allowInsecure=false` through ARM, then verify HTTP refusal/redirection, the exact registered HTTPS OIDC redirect, ignored spoofed forwarding headers, rejection of a non-canonical Host and successful canonical-host probes. These checks require no inference. The proposed Log Analytics 0.1 GB cap also needs actual service acceptance/effective-value verification; caps can overshoot and stop logging and are not budget limits. Provider/API availability (including the pinned ACR preview API), regional capacity, policy, name/address availability, DNS/private connectivity, identity/RBAC propagation, Graph bootstrap, protected digest builds, release-client role sufficiency, and actual Linux/ingress/probe behaviour need execution-owner checks under separate authorisation. No deployment validation, what-if, cloud write, inference, sign-in or permission mutation is performed by this implementation or its validator.
