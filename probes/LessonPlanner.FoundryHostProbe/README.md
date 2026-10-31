# Foundry Responses hosting capability probe

## Purpose and current evidence

This isolated project compiles the **builder-level** `AddFoundryResponses` and `MapFoundryResponses` APIs from `Microsoft.Agents.AI.Foundry.Hosting` **1.27.0-preview.261030.1**. The main application does not reference this project or its preview dependency graph. Its package lock records the actual resolution.

The default executable path prints assembly information and exits. It does not construct a credential, start a listener, register an agent or make a network request. Local compilation and this default path are permitted before cloud authorisation.

```powershell
dotnet restore probes\LessonPlanner.FoundryHostProbe --locked-mode
dotnet build probes\LessonPlanner.FoundryHostProbe --no-restore
dotnet run --project probes\LessonPlanner.FoundryHostProbe --no-build -- --check
```

The project uses `Azure.AI.Projects.AIProjectClient` and the current `Azure.AI.Extensions.OpenAI` namespace. The **model-based** `AIProjectClient.AsAIAgent` overload constructs a non-versioned application agent backed by the project Responses API. It does not create a service-managed agent registration. `AddFoundryResponses` exposes a hosting protocol; it does not deploy a container.

The separate test project exercises configuration rejection and model-adapter limits using a synthetic in-process client. It acquires no Azure credential and sends no inference request:

```powershell
dotnet test tests\LessonPlanner.FoundryHostProbe.Tests --no-build
```

## Explicitly gated execution

Do not execute `--serve` until the responsible operator has separately approved the existing project, identity, model deployment, consumption and requested hosting test. Setting the acknowledgement below is a technical guard, **not authorisation**. There is no automatic discovery, provisioning, role assignment, registration or alternate Foundry resource.

The execution configuration consists solely of environment variables:

- `Probe__ExecutionAcknowledgement`: exact value `existing-project-inference-approved`.
- `Probe__ProjectEndpoint`: an approved existing `https://<account>.services.ai.azure.com/api/projects/<project>` URL, with no query, fragment or credentials.
- `Probe__Deployment`: the approved existing deployment name.
- `Probe__Credential`: either `AzureCli` or `ManagedIdentity`. There is no credential chain or API-key option.
- `Probe__TenantId`: required GUID when using `AzureCli`; uses the already selected CLI account in that tenant and does not sign in or switch accounts.
- `Probe__ManagedIdentityClientId`: required user-assigned identity GUID when using `ManagedIdentity`.

Only after authorisation and configuration:

```powershell
dotnet run --project probes\LessonPlanner.FoundryHostProbe --no-build -- --serve
```

Starting the acknowledged mode can initialise platform storage/authentication and receiving a model request can incur usage. Do not run that mode merely to check local wiring. The unacknowledged mode exits with code 2 before configuration, credential construction or hosting and can be checked locally with the acknowledgement absent.

Outside Foundry, the host binds to `127.0.0.1:8088`. Within the real hosted environment, the SDK controls binding using `FOUNDRY_HOSTING_ENVIRONMENT` and `PORT`. The probe is not part of the workshop's public Web or MCP service and must never replace their authentication boundaries.

Execution is deliberately constrained:

- one accepted `POST /responses` per process, including an unsuccessful request;
- a 16 KiB request-body limit and a 30-second request-timeout policy;
- the approved deployment, 128 output tokens and no tools at the model-client adapter;
- a two-minute shutdown timer;
- no browser Origin, DELETE, PATCH or PUT requests;
- stored-output, resilient-background and steerable-conversation features disabled;
- generic HTTP failures and no prompt/content logging added by this probe.

The SDK's protocol readiness and stored-output safeguards remain enabled. `/readiness` is SDK-owned. `/probe-scope` identifies this constrained test. These are wiring checks, not evidence of model rights or service compatibility. Platform storage and ingress must be configured by the authorised hosting environment; this project does not provision them.

## Acceptance still required

The hosting package targets **Foundry Responses container protocol v2.0**. It is not a v1 compatibility adapter. After explicit approval, record each observed boundary separately:

1. The selected identity can access the existing project's data plane.
2. The selected deployment accepts the intended typed-output and tool-calling patterns. This no-tools probe alone cannot establish those application requirements.
3. The existing Agent Service supports this exact C# package/protocol and its platform storage contract.
4. A separately authorised hosted registration/deployment is reachable through the intended authenticated service route.
5. One bounded synthetic invocation completes, with observed consumption and failure handling recorded.

A successful local build does not satisfy these cloud checks. The main application retains its **application-hosted** provider unless an authorised compatibility test establishes the hosted path. A failed capability test must not cause silent fallback or an attempt to create a replacement project.

## Version-specific references

- [Agent Framework 1.27.0 release](https://github.com/microsoft/agent-framework/releases/tag/dotnet-1.27.0).
- [Builder registration and endpoint mapping](https://github.com/microsoft/agent-framework/blob/dotnet-1.27.0/dotnet/src/Microsoft.Agents.AI.Foundry.Hosting/ServiceCollectionExtensions.cs).
- [Hosting options and defaults](https://github.com/microsoft/agent-framework/blob/dotnet-1.27.0/dotnet/src/Microsoft.Agents.AI.Foundry.Hosting/FoundryResponsesOptions.cs).
- [Model-based project agent construction](https://github.com/microsoft/agent-framework/blob/dotnet-1.27.0/dotnet/src/Microsoft.Agents.AI.Foundry/AIProjectClientExtensions.cs).
- [Protocol version declaration](https://github.com/microsoft/agent-framework/blob/dotnet-1.27.0/dotnet/src/Microsoft.Agents.AI.Foundry.Hosting/Microsoft.Agents.AI.Foundry.Hosting.csproj).

Stable packages may expose experimental APIs. `MAAI001` suppression is limited to the exact preview hosting calls and assembly inspection. It is not disabled across the solution. The older service-collection overload still appears in some samples; this probe uses the builder overload required by the pinned release.
