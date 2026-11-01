# Production identity bootstrap

This is an operator contract for the separate presenter and MCP identities. It does not execute registrations or grant permissions. Use the approved deployment plan, private parameter files and a private applied-object ledger. Do not put real tenant, subscription, application, principal or presenter identifiers in this repository or public workflow logs. Bootstrap is separate from routine build and release.

## Prerequisites and stop conditions

- Confirm the approved existing human account, tenant and subscription without switching to another identity. A successful sign-in or Graph profile read does not establish administrative authority.
- Establish currently active authority for each registration, federation, service-principal and application-role operation. Azure subscription roles do not establish Entra directory authority; eligible roles are not active rights. Do not activate roles, add Graph permissions to the runtime apps or broaden a grant to pass a check.
- Respect interactive authentication and Conditional Access challenges. The user completes credentials and MFA personally. Stop on a challenge; do not clear caches, substitute credentials or retry a write whose outcome is unknown.
- Obtain the actual Web user-assigned managed identity (UAMI) client and principal IDs from the approved foundation outputs. They are different identifiers.
- Establish the exact canonical HTTPS origin before enabling production presenter authentication. Read ACA ingress back with `allowInsecure=false`, verify HTTP refusal/redirection and the exact HTTPS OIDC callback. The Production fixed-origin boundary consumes no forwarded headers or proxy-network trust. Spoofed protocol/host/client-IP headers must not affect it; see [the ingress contract](configuration.md#public-url-ingress-and-limits).
- Respect the approved operating period and resource/model limits. Template compilation and this document are not deployment or live-acceptance evidence.

## Identifier mapping

| Private value | Used for |
| --- | --- |
| Presenter application object ID | Graph application updates and its federated-credential parent |
| Presenter application client ID | `AzureAd__ClientId` |
| MCP application object ID | Graph API application updates |
| MCP application client ID | API identifier URI, token scope and v2 `aud` |
| MCP service-principal object ID | Resource of the `Quiz.Publish` app-role assignment |
| Web UAMI principal/object ID | Presenter federation `subject` and app-role assignment `principalId` |
| Web UAMI client ID | Runtime managed-identity selection and MCP `azp` allowlist |
| Presenter user object ID | `Presenter__AllowedObjectIds__0` |

Record both the `id` and `appId` returned by each application and service-principal creation. Do not infer one from the other or identify an existing object solely by its display name. Retain confirmed object/assignment IDs and scopes so a resumed bootstrap can reconcile its state. If a creation response is lost, inspect the exact intended object before attempting another creation.

## Presenter application

Create one single-tenant application and its service principal through an approved administrative route. Its Microsoft Graph application settings are:

- `signInAudience`: `AzureADMyOrg`.
- `isFallbackPublicClient`: `false`.
- `requiredResourceAccess`: `[]`; the application does not call Microsoft Graph.
- `web.redirectUris`: the exact canonical HTTPS `/signin-oidc` and authorised `/signout-callback-oidc` callback URLs.
- `web.implicitGrantSettings.enableAccessTokenIssuance`: `false`.
- `web.implicitGrantSettings.enableIdTokenIssuance`: `false`.
- No SPA/native redirect, delegated API permissions, password or certificate credential.

The application uses confidential OpenID Connect code flow with PKCE. Disabling implicit ID-token issuance does not disable the code-flow ID token. Presenter authorisation remains the application's tenant and user-object allowlist, with a fixed two-hour non-sliding cookie and antiforgery-protected sign-out.

Add exactly one federated identity credential under the **presenter application object**, using `POST /v1.0/applications/<presenter-application-object-id>/federatedIdentityCredentials`:

```json
{
  "name": "web-managed-identity-assertion",
  "issuer": "https://login.microsoftonline.com/<tenant-id>/v2.0",
  "subject": "<web-uami-principal-object-id>",
  "audiences": ["api://AzureADTokenExchange"]
}
```

The application and managed identity belong to the same tenant. The subject is the UAMI **principal ID**, not its client ID. Read back all fields; a successfully created but incorrectly configured federation will still fail token exchange.

Set the sole production credential entry to:

```text
AzureAd__ClientCredentials__0__SourceType=SignedAssertionFromManagedIdentity
AzureAd__ClientCredentials__0__ManagedIdentityClientId=<web-uami-client-id>
AzureAd__ClientCredentials__0__TokenExchangeUrl=api://AzureADTokenExchange/.default
```

This trust authenticates the presenter application's confidential-client exchange. It does not itself grant the managed identity MCP or Foundry access.

## Independent MCP API

Create the second single-tenant application and its service principal. Once its actual client ID is known, set `identifierUris` to exactly `api://<mcp-application-client-id>`. No presenter callback belongs on this API. Keep its `requiredResourceAccess` empty, public-client fallback disabled and both implicit-grant flags false.

Set its API and application-role definitions as follows. Allocate and retain the custom role GUID in the private deployment parameters; the placeholder is deliberately not deployable.

```json
{
  "api": {
    "requestedAccessTokenVersion": 2,
    "oauth2PermissionScopes": [],
    "preAuthorizedApplications": []
  },
  "appRoles": [
    {
      "id": "<quiz-publish-role-guid>",
      "allowedMemberTypes": ["Application"],
      "displayName": "Publish an approved quiz",
      "description": "Publish a server-validated presenter-approved quiz draft.",
      "isEnabled": true,
      "value": "Quiz.Publish"
    }
  ]
}
```

On the API **service principal**, set `appRoleAssignmentRequired=true`. Grant the one custom application role to the **Web UAMI**, not the presenter application or user. The supported resource-side Graph operation is `POST /v1.0/servicePrincipals/<mcp-service-principal-object-id>/appRoleAssignedTo`:

```json
{
  "principalId": "<web-uami-principal-object-id>",
  "resourceId": "<mcp-service-principal-object-id>",
  "appRoleId": "<quiz-publish-role-guid>"
}
```

This is a separate Entra application-role grant, not one of the Azure RBAC assignments. Do not grant a Microsoft Graph role, delegated scope, tenant-wide consent or role to another principal. Read back the exact assignment and verify that the enabled API role matches before attempting application access.

Runtime mapping:

- Web `Mcp__TokenScope`: `api://<mcp-application-client-id>/.default`.
- Web `Mcp__ManagedIdentityClientId`: Web UAMI client ID.
- Both hosts `Mcp__CallerClientId`: Web UAMI client ID.
- MCP `Mcp__Audience`: MCP application **client-ID GUID**, not the `api://` scope URI.
- MCP `Mcp__TenantId`: the one approved tenant.
- MCP `Mcp__RequiredRole`: `Quiz.Publish`.

The server requires the tenant-specific v2 issuer, exact `aud`, Web UAMI `azp`, application role and no delegated `scp`. It independently validates the stored presenter approval, action, draft/version/hash, caller, expiry and idempotency. Internal HTTPS ingress and a model-produced approval flag do not grant publication authority.

## Build and release identities

The foundation manages the separate build and release UAMIs and their two GitHub federations. Those trusts use issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange` and the exact repository/environment subject. Read the repository's non-secret OIDC configuration to establish its subject format before creating either trust. This repository uses [immutable subjects](https://docs.github.com/en/actions/reference/security/oidc#immutable-subject-claims), including its public owner and repository IDs. The [infrastructure contract](../infra/README.md#fixed-footprint) lists both exact subjects; the CI guard must use the same prefix. Do not disable immutable subjects, print tokens or use a plain-name fallback. Environments are `lumero-build` and `lumero-release`; branch restrictions and human review are separate protections because an environment subject alone does not restrict a branch.

Keep build registry permissions, runtime pulls/data access and the narrow two-app release assignments separate. The release role permits container-app reads/writes and revision reads only. `containerApps/write` is not field-level image-only authority; protected workflow review and its focused implementation remain necessary. If an update or polling path requires an additional action, stop and present the missing action instead of broadening the role.

## Verification and receipts

Before inference, retain non-sensitive pass/fail evidence for the actual HTTPS-only ingress, canonical Host, ignored spoofed forwarded headers, exact callbacks, secure cookie and antiforgery, presenter denial outside the allowlist, correct app-only MCP audience/caller/role, private storage and managed-identity access. Never publish tokens, key-ring contents, prompts or credential material. Negative cases must stay within the approved identities and limits.

Keep bootstrap receipts distinct from local synthetic tests and from later real model, publication, persistence/restart and external-device acceptance. A directory grant alone is not an end-to-end result. See [application configuration](configuration.md) and the [infrastructure contract](../infra/README.md).

## First-party references

- [Application manifest and web authentication properties](https://learn.microsoft.com/entra/identity-platform/reference-microsoft-graph-app-manifest).
- [Configure an application to trust a managed identity](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity).
- [Microsoft.Identity.Web certificateless credentials](https://learn.microsoft.com/entra/msidweb/authentication/certificateless).
- [API token version and delegated-scope properties](https://learn.microsoft.com/graph/api/resources/apiapplication?view=graph-rest-1.0).
- [Grant an application role through the resource service principal](https://learn.microsoft.com/graph/api/serviceprincipal-post-approleassignedto?view=graph-rest-1.0).
- [GitHub OIDC with Azure](https://learn.microsoft.com/azure/developer/github/connect-from-azure-openid-connect).
