# Application configuration

The committed settings contain placeholders, not a usable cloud environment. Supply values through deployment settings or local .NET user secrets. Keep tenant, principal and subscription identifiers out of public examples. Infrastructure, permission changes, charged inference and deployment require separate approval.

## Presenter authentication

The Web service uses Microsoft.Identity.Web with a single-tenant OpenID Connect code flow, PKCE and an explicit allowlist of one to twenty presenter object identifiers.

Required settings:

- `AzureAd__Instance`: `https://login.microsoftonline.com/`.
- `AzureAd__TenantId`: the approved tenant.
- `AzureAd__ClientId`: the confidential Web application registration.
- `AzureAd__ClientCredentials__0__SourceType`: `SignedAssertionFromManagedIdentity`.
- `AzureAd__ClientCredentials__0__ManagedIdentityClientId`: the Web workload's user-assigned identity client ID, or an empty value for system-assigned identity.
- `AzureAd__ClientCredentials__0__TokenExchangeUrl`: `api://AzureADTokenExchange/.default` for the documented production federation.
- `Presenter__AllowedObjectIds__0`, with subsequent numeric entries when required.

Exactly one explicit credential is accepted. Production requires an authorised managed-identity federation relationship for the confidential client. Client secrets and legacy certificate collections are rejected. An explicitly configured `Path` credential and certificate disk path are permitted only in Development or Testing. A local certificate does not grant MCP application permissions.

Registration follows Microsoft's [certificateless web-app guidance](https://learn.microsoft.com/entra/msidweb/authentication/certificateless): `AddMicrosoftIdentityWebApp(...).EnableTokenAcquisitionToCallDownstreamApi().AddInMemoryTokenCaches(...)`. In the pinned Microsoft.Identity.Web 4.16.1 implementation, token acquisition installs the MSAL authorisation-code redemption handler, which applies the configured managed-identity client assertion and PKCE verifier. `AddMicrosoftIdentityWebApp` alone does not attach that assertion to code redemption. No downstream delegated API scopes are requested; the normal OpenID Connect scopes are `openid`, `profile` and `offline_access`. Foundry and MCP continue using separate workload-identity tokens.

Raw `tid` and `oid` claims are retained by setting `MapInboundClaims=false` on both Microsoft.Identity.Web's named options and the OIDC options. The pinned identity library copies its token handler into the OIDC options, so changing only the latter flag does not reliably change the handler's claim mapping. MSAL marks code redemption as handled; the pinned ASP.NET Core OIDC handler then skips its usual token-response protocol validation. The application invokes the standard protocol validator during token validation to retain the nonce check, while preserving Microsoft's token-acquisition and home-account handlers. It does not replace token signature, issuer, audience or lifetime validation.

The approved single-Web-replica deployment uses a dedicated **8 MiB, two-hour maximum-lifetime in-memory MSAL serialisation cache**. The limit measures serialised entry bytes, not total process memory or transient MSAL allocations. It does not constrain unrelated framework caches. `UseFastUnboundedCache` is forced off, so that opt-in cannot bypass the serialisation cache. Tokens are not saved in the presenter cookie. Cache entries may be evicted and are lost on process replacement; the separately persisted Data Protection key ring and application records are unchanged. Existing valid presenter cookies do not depend on retaining this cache. Before adding delegated API operations or scaling beyond one Web replica, replace this cache with an approved, genuinely shared and protected distributed token cache and verify rolling-replacement behaviour. `AddDistributedMemoryCache` alone is not shared across processes. See Microsoft's [token-cache guidance](https://learn.microsoft.com/entra/msidweb/authentication/token-cache-overview).

Authentication failures redirect to `/sign-in-failed`, which returns a generic 503 page and a validated support reference. The application logs that generated reference, the failure type and a canonical provider correlation GUID when available, without the exception body or authentication material. Invalid reference parameters and provider error descriptions are not rendered. `/access-denied` remains the separate 403 authorisation-denial page.

Configure the Web application's exact HTTPS `/signin-oidc` redirect URI and any authorised sign-out callback separately. The [identity bootstrap contract](identity-bootstrap.md) distinguishes application object IDs, client IDs and managed-identity principal IDs. Cookie lifetime is fixed at two hours without sliding renewal. Production cookies are Secure and HttpOnly; presenter identity is revalidated against the current tenant and allowlist. Sign-out is an antiforgery-protected POST.

## Independent MCP API

The MCP service has its own API registration and application role. It validates signature, expiry, tenant-specific v2 issuer, exact API audience, `azp`, application role and the absence of delegated `scp` claims. Internal ingress is not authentication.

MCP service settings:

- `Mcp__TenantId`: the approved single tenant.
- `Mcp__Audience`: the API client ID expected in a v2 access token's `aud` claim.
- `Mcp__CallerClientId`: the Web workload managed identity allowed in `azp`.
- `Mcp__RequiredRole`: `Quiz.Publish`, unless an explicitly agreed application-role value is supplied.

Web settings:

- `Mcp__Endpoint`: the internal HTTPS MCP endpoint, ending in `/mcp`.
- `Mcp__TokenScope`: the API application ID URI followed by `/.default`, for example `api://<mcp-api-client-id>/.default`.
- `Mcp__CallerClientId`: the same authorised caller identity used by the MCP policy.
- `Mcp__ManagedIdentityClientId`: the Web token-acquisition identity, or empty for system-assigned identity.

The API must issue **v2 access tokens**. Its role must allow application principals and be assigned to the authorised Web identity. Do not reuse a Foundry token or infer application access from a successful interactive Azure CLI login.

The official MCP client sends presenter assertions outside model-controlled tool arguments. The service accepts those assertions only from the authenticated allowlisted application and matches them against the stored approval. Approval binds action, draft, version, digest, presenter, caller and expiry. An LLM cannot approve publication.

The service rejects browser Origin headers on `/mcp`. HTTP requests, response streams, timeouts and per-caller rates are bounded. Redirects are disabled on the client. Publication retry returns a persisted outcome without creating another quiz.

## Durable state and keys

Both executables use:

- `Storage__TableServiceUri`: HTTPS Table service endpoint without credentials or a query string.
- `Storage__TableName`: `lessonplanner` by default.
- `Storage__ManagedIdentityClientId`: that executable's workload identity, or empty for system-assigned identity.

The Table must already exist. Application startup does not create production resources. Scope the storage data-role assignments to the approved state resources. The Web and MCP executables must use the same Table and canonical quiz policy.

A quiz's `draft`, `approval`, `publication` and `attempt-<id>` rows share one `quiz-<draft-id>` partition. Approval consumption/publication and attempt-budget/result creation each use a same-table, same-partition atomic transaction. Conditional ETags protect concurrent writers. Lessons use separate `lesson-<id>` partitions. A draft's parent lesson must already be Ready and share its owner and provenance.

Web key-ring settings:

- `DataProtection__BlobUri`: the exact private key-ring Blob URL, without a SAS token.
- `DataProtection__ManagedIdentityClientId`: the Web Blob-access identity, or empty for system-assigned identity.

Provision the private container and authorised Blob data access separately. The key ring must survive Web replacement and be shared by Web replicas. Treat the key ring as sensitive material. The application name is `Lumero.LessonPlanner.v1`; changing it invalidates existing cookies and submission tickets. Local development uses an ignored file key ring.

Table readiness checks make a bounded read. They do not invoke a model, publish a quiz or create resources. A healthy storage probe does not establish Foundry access or end-to-end application readiness.

## Foundry provider

- `Foundry__Endpoint`: the approved resource's HTTPS Azure OpenAI v1 endpoint.
- `Foundry__Deployment`: an approved existing model deployment supporting the required typed output and tool behaviour.
- `Foundry__ManagedIdentityClientId`: the Web inference identity, or empty for system-assigned identity.

Production explicitly uses managed identity. Development and Testing explicitly use Azure CLI credentials for real-provider configuration. There is no broad credential-chain or API-key fallback. The separate local harness replaces model services only after the production service composition is registered; its replacements live in test assemblies.

Each role is an application-hosted Agent Framework agent. Application code owns role order, deterministic checks, the two-revision ceiling and the action gate. Service-managed registration and hosted deployment are not implied by `AsAIAgent`, package restoration or a configured deployment name.

## Public URL, ingress and limits

- `Quiz__PublicBaseUrl`: the canonical externally reachable HTTPS origin, with no extra path, query, fragment or user information.
- `Quiz__LifetimeHours`: 24 by default; permitted duration is five minutes to seven days.
- `Quiz__ApprovalMinutes`: 5 by default; permitted duration is thirty seconds to ten minutes.
- `Quiz__MaximumAttempts`: 200 by default; range is 1 to 1000.
- `ReverseProxy__UseFixedHttpsOrigin`: defaults to `true` in Production and acts only in Production. The deployment explicitly enables it.
- `AllowedHosts`: exactly the host in `Quiz__PublicBaseUrl` for the production Web service. No wildcard, extra hostname or nonstandard authority port is permitted.

Production startup validates both settings, even when the fixed-scheme flag is disabled. The canonical origin must contain a non-loopback ASCII DNS hostname; IP literals, trailing dots and ambiguous paths are rejected. Requests may use the canonical host case-insensitively with an optional explicit `:443`; the boundary normalises that authority. With the flag disabled, strict Host validation remains and the existing HTTP-to-HTTPS redirect runs before authentication.

The Web production boundary sets the request scheme to HTTPS before other application middleware, then rejects a non-canonical Host. It reads no `X-Forwarded-*` header and establishes no proxy-network or client-address trust. This follows [ASP.NET Core's fixed-scheme guidance when every external request is secure](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0#when-it-isnt-possible-to-add-forwarded-headers-and-all-requests-are-secure). The edge must enforce HTTPS. For ACA, configure `allowInsecure=false` and verify the effective setting before production authentication. Do not deploy this mode behind an edge that accepts insecure external traffic. The quiz origin remains server configuration, never a request-derived value.

Health probes send the exact configured Host, including the canonical Web Host on the internal HTTP probe. There is no hostname exception for probe paths. Local Development and Testing keep their normal request schemes.

The Web process allows at most two concurrent model operations and one per presenter. Model operations also have a six-per-ten-minute presenter rate limit. Public operations share one **600-request-per-minute fixed-window partition with no queue**, independently of direct IP or forwarded headers. This accommodates at least sixty active participants submitting within a minute with request headroom; it does not permit sixty requests to execute simultaneously. The Web HTTP concurrency limit remains thirty-two with no queue. The MCP service permits 120 requests per minute per caller. Rate and model-concurrency controls are per process and reset on replacement; the durable quiz attempt budget and transactional idempotency apply across replicas. The fixed deployment uses one replica per app.

Generation has a handler-local 210-second cooperative deadline. Its linked cancellation token is created inside the handler's error boundary before reading the form, then passed to both form reading and generation. Quiz drafting remains 180 seconds; publication remains 120 seconds with up to ten seconds of durable recovery. Client cancellation still propagates and releases the model-operation permit.

Against an ingress timeout of 240 seconds, Generate has thirty seconds of nominal headroom. Middleware and antiforgery execute before this budget starts and may already read the form. This is not an ingress-arrival deadline or a guaranteed wire-response margin. Cancellation cannot guarantee that remote model processing or billing stops.

Native form size and count are bounded. Anonymous submissions use antiforgery plus a protected, short-lived quiz/attempt ticket. Results disclose marking information only after a recorded submission and possession of its unpredictable result identifier.

QR generation requires a committed publication and a successful bounded fetch of its exact canonical public page, including the expected quiz identity header. An unreachable public link does not roll back a committed publication. The interface should display the link and a reachability warning until it can supply a QR image.

## Logging and data handling

Default logging is Warning. Do not enable HTTP bodies, identity tokens, sensitive Agent Framework diagnostics, prompt transcripts or hidden reasoning in application telemetry. Activity shown to the presenter is limited to safe workflow events and persisted state. Participant records contain no names, email addresses or IP addresses. The shared anonymous rate partition does not use an IP address.

Local model outputs, local sign-in routes and test signing material belong only to the test harness. Never deploy `LessonPlanner.LocalHarness`, reference the integration-test assembly from production, or expose an emulator to a public interface.
