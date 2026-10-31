# Stack and integration contract

## Scope of the first checkpoint

This checkpoint establishes a pinned .NET 10 solution and typed application boundaries. It does not establish a working lesson generator, deployment, sign-in or model access. Application and cloud verification are separately recorded.

- SDK: 10.0.403, strict SDK selection in `global.json`.
- Microsoft Agent Framework core, OpenAI adapter and workflows: 1.27.0.
- Official C# MCP client and ASP.NET Core transport: 2.3.0.
- Aspire AppHost SDK and hosting package: 13.6.4.
- Direct versions: `Directory.Packages.props`; transitive resolution: committed NuGet lock files.
- `System.Security.Cryptography.Pkcs` is pinned to 10.0.14 to resolve the vulnerabilities reported by NuGet audit in the identity library's 10.0.10 transitive floor. Audit remains enabled.
- Libraries: Azure.Data.Tables, Open XML SDK (MIT) for editable DOCX, QRCoder (MIT) for local PNG encoding. No document service or QR tracking service is required.

`src/LessonPlanner.Core/Contracts.cs` and `Boundaries.cs` define schema `lumero.v1`. Domain contracts are independent of Azure and Agent Framework. Public quiz questions cannot contain correct-option identifiers or explanations. Source references retain the canonical URL, retrieval time and SHA-256 digest.

## Process and configuration boundary

The web and MCP executables each listen on container port **8080**. The MCP service uses internal ingress and stateless Streamable HTTP at **/mcp**. Authentication is required even on internal ingress. Liveness is **/health/live**; readiness is **/health/ready**. Infrastructure and deployment files are owned separately from application implementation.

Configuration names reserved for integration:

- `AzureAd__Instance`, `AzureAd__TenantId`, `AzureAd__ClientId`: presenter single-tenant OpenID Connect. Production requires `AzureAd__ClientCredentials__0__SourceType=SignedAssertionFromManagedIdentity` and an authorised confidential-client federation relationship. Set `AzureAd__ClientCredentials__0__ManagedIdentityClientId` for a user-assigned identity. Explicit certificate-path credentials are local-only; client secrets and legacy fallback collections are rejected.
- `Presenter__AllowedObjectIds__0`: explicit presenter object allowlist, checked in server operations.
- `Mcp__Endpoint`: absolute HTTPS URL ending in `/mcp`.
- `Mcp__Audience`, `Mcp__TenantId`, `Mcp__CallerClientId`, `Mcp__RequiredRole` (default `Quiz.Publish`): distinct API audience, issuer, calling web managed-identity client ID and required application role.
- `Mcp__TokenScope`: that API's application scope ending in `/.default`. Never reuse a Foundry token.
- `Mcp__ManagedIdentityClientId`: web workload identity used for MCP tokens.
- `Storage__TableServiceUri`, `Storage__TableName` (default `lessonplanner`), `Storage__ManagedIdentityClientId`: Entra Azure Table access. The Table must exist before production startup.
- `Storage__ConnectionString`: development emulator only, rejected outside Development/Testing and limited to loopback Azurite. Not a production key option.
- `DataProtection__BlobUri`: durable cookie/CSRF key ring. `DataProtection__ManagedIdentityClientId`: Blob identity. Provision the private container separately.
- `ReverseProxy__KnownNetworks__0`: explicit trusted ingress CIDR. The Web service accepts forwarded protocol and client address through one trusted hop. Do not trust an Internet-wide network.
- `Foundry__Endpoint`: Azure OpenAI v1 HTTPS model endpoint for application-hosted inference. `Foundry__Deployment`, `Foundry__ManagedIdentityClientId`: approved deployment and identity. No model endpoint or credentials are sent to browsers.
- `Quiz__PublicBaseUrl`: canonical externally reachable HTTPS web origin, never derived from a request Host header. `Quiz__LifetimeHours` defaults to 24; `Quiz__ApprovalMinutes` defaults to 5; `Quiz__MaximumAttempts` defaults to 200.

All public configuration examples must contain placeholders only. Infrastructure must provide separate web/MCP identities, Table data access, Blob data access for the web key ring, the MCP application-role assignment, and existing Foundry inference access. No new Foundry project is part of the application contract.

## Publication transaction

A draft is immutable and server owned. Its identifier is cryptographically random. Version, content, marking keys, provenance, owner and review contribute to its digest. Presenter approval is a short-lived stored grant bound to this digest and the allowed MCP caller. The agent receives an approved reference, not an `approved` boolean.

One Table and partition (the draft ID) contain the draft, approvals, publication receipt and attempts/results. Consuming approval and creating the publication row use one entity group transaction with conditional ETags and Add semantics. A request cannot publish a different version, user or hash. A repeated valid invocation returns its own persisted receipt. Anonymous projection starts only when a committed publication exists.

The trusted web caller derives presenter identity from Entra sign-in and supplies it outside model-controlled arguments. The MCP service validates its Entra application token, audience, issuer, allowed caller, application role and stored grant. Compromise of the authorised web service is outside the protection supplied by the model approval boundary.

## Foundry hosting decision and gated probe

The reliable initial path is application-hosted Agent Framework agents calling Foundry models with Entra credentials. `AsAIAgent` constructs an application object; it does not register a service-managed agent. The deterministic application owns role order, validation, revision budgets and approval.

After explicit Azure approval, test the existing project's supported Agent Service path in isolation: verify Entra data-plane access, the selected deployment's structured output and tool support, then a bounded hosted agent invocation. Record observed result and consumption. Do not infer compatibility from region or deployment existence.

Agent Framework 1.27.0 [release notes](https://github.com/microsoft/agent-framework/releases/tag/dotnet-1.27.0) require hosted applications to use `builder.AddFoundryResponses(...)`. The service-collection overload is for local/non-hosted use. The [isolated probe](../probes/LessonPlanner.FoundryHostProbe/README.md) compiles that builder API and `MapFoundryResponses` against hosting package **1.27.0-preview.261030.1**. Its actual locked graph includes `Azure.AI.Projects` and **`Azure.AI.Extensions.OpenAI` 3.0.0-beta.5**, AgentServer Core 1.0.0-beta.30 and Responses 1.0.0-beta.9. The Invocations package is not needed by this probe. These preview dependencies remain outside the application dependency graph.

This hosting adapter targets Responses container protocol **v2.0**, not v1. The default probe executable prints assembly information and exits without credentials, hosting or network activity. Hosted execution and actual service compatibility remain separately gated. Stable Agent Framework packages can still expose experimental APIs. Never blanket-suppress diagnostics or claim hosted verification from compilation.

No Azure provisioning, role assignment, agent registration, inference call or deployment is authorised by this checkpoint.
