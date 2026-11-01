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
- `ReverseProxy__KnownNetworks__0`: the actual trusted ingress CIDR; add explicit numeric entries as necessary.

Configure the trusted proxy addresses from the real ingress topology. Forwarded protocol and client address are accepted through one trusted hop. Do not add an Internet-wide trusted network to make redirects work. Ensure the edge restricts accepted hostnames, and configure ASP.NET `AllowedHosts` where compatible with the platform's health-probe Host header. The quiz origin is never inferred from a request Host header.

The Web process allows at most two concurrent model operations and one per presenter. Model operations also have a six-per-ten-minute presenter rate limit. Public operations use a sixty-per-minute IP limit. The Web HTTP concurrency limit is thirty-two with no queue. The MCP service permits 120 requests per minute per caller. Rate and model-concurrency controls are per replica; the durable quiz attempt budget and transactional idempotency apply across replicas. Set an approved replica ceiling and platform limits before cloud use.

Generation has a handler-local 210-second cooperative deadline. Its linked cancellation token is created inside the handler's error boundary before reading the form, then passed to both form reading and generation. Quiz drafting remains 180 seconds; publication remains 120 seconds with up to ten seconds of durable recovery. Client cancellation still propagates and releases the model-operation permit.

Against an ingress timeout of 240 seconds, Generate has thirty seconds of nominal headroom. Middleware and antiforgery execute before this budget starts and may already read the form. This is not an ingress-arrival deadline or a guaranteed wire-response margin. Cancellation cannot guarantee that remote model processing or billing stops.

Native form size and count are bounded. Anonymous submissions use antiforgery plus a protected, short-lived quiz/attempt ticket. Results disclose marking information only after a recorded submission and possession of its unpredictable result identifier.

QR generation requires a committed publication and a successful bounded fetch of its exact canonical public page, including the expected quiz identity header. An unreachable public link does not roll back a committed publication. The interface should display the link and a reachability warning until it can supply a QR image.

## Logging and data handling

Default logging is Warning. Do not enable HTTP bodies, identity tokens, sensitive Agent Framework diagnostics, prompt transcripts or hidden reasoning in application telemetry. Activity shown to the presenter is limited to safe workflow events and persisted state. Participant records contain no names, email addresses or IP addresses. IP addresses are used transiently by the rate limiter.

Local model outputs, local sign-in routes and test signing material belong only to the test harness. Never deploy `LessonPlanner.LocalHarness`, reference the integration-test assembly from production, or expose an emulator to a public interface.
