# Application-owned agents

This assembly implements the five production Microsoft Agent Framework role adapters and the deterministic `ILessonWorkflow`. It does not provide a demonstration model, offline lesson fallback, storage, authentication middleware or MCP transport.

## Host registration

Register `ICurriculumRetriever`, `ILessonStore`, `IQuizStore` and `IMcpPublicationClient`, then call:

```csharp
services.AddLessonAgents(configuration, environment);
```

The configuration shim requires `Foundry:Endpoint` and `Foundry:Deployment`. `Foundry:ManagedIdentityClientId` is optional and, when supplied, must be a non-empty GUID in canonical hyphenated format. `Development` and `Testing` explicitly use `AzureCliCredential`. Every other environment uses `ManagedIdentityCredential`, selecting the configured user-assigned identity or the system-assigned identity when no client ID is supplied. There is no credential chain or fallback. This shim owns the `TokenCredential` registration, replacing any previous registration, and retains the existing execution limits.

Hosts that own their credential registration or require role-specific deployment settings can instead register their Entra `TokenCredential` and call the typed API:

```csharp
services.AddLessonPlannerAgents(
    new FoundryAgentOptions(endpoint, deploymentName),
    new AgentExecutionLimits());
```

`TimeProvider.System` is registered only when a clock has not already been provided. Roles and the coordinator are scoped; the stateless Responses provider is a singleton. The infrastructure-owned `OfficialMcpPublicationClient` implements the publication dependency. The host must derive `PublicationCaller` from verified authentication, not a request body or model output.

`FoundryAgentOptions.RoleDeployments` optionally selects a separate deployment per model role. The provider accepts Azure resource Responses endpoints and normalises `/` or `/openai/v1` to `/openai/v1/`. Foundry project URLs, credentials in URLs, non-HTTPS endpoints and unrecognised hosts are rejected. API keys and connection-string authentication are not supported.

## Provider and hosting boundary

`FoundryResponsesAgentProvider` uses the pinned official OpenAI Responses client with an Entra bearer-token policy and creates a fresh, named `AIAgent` for each invocation. This is **application-hosted execution**, not registration of a Foundry service-hosted agent. `IRoleAgentProvider` isolates the future hosting choice. There are no hosted-preview package dependencies or implicit provider fallbacks.

The pinned stable OpenAI package marks the authentication constructor and `GetResponsesClient` experimental. Two expression-scoped `OPENAI001` pragmas document that deliberately isolated API surface. Other compiler warnings remain enabled. No model request occurs during registration or agent construction.

## Evidence and review boundaries

- Orchestrator chooses only IDs from the retriever's current catalogue. The application fixes role order and revision limits.
- Curriculum really retrieves every selected source before model analysis. The original catalogue URL, title, retrieval timestamp, hash and extract remain server-owned. A document hash is not recomputed from extracted text. Missing, mismatched or oversized evidence fails explicitly.
- Writer output has no source-reference field. The application attaches exact retrieved references, and the coordinator stamps them again before validation. Objectives can select citation IDs but cannot supply URLs or provenance.
- Reviewer receives the candidate and evidence independently, without the writer's session or previous review. Deterministic structure, timings, citation and review checks remain authoritative.
- Quiz drafting has no tools. Exactly five MCQs, their options and answer keys must pass deterministic checks and independent review before persistence.

Model JSON rejects missing required fields, extra or duplicate properties, null values, invalid enum values, excessive depth, output oversize, links and unsupported control characters. Prompts identify retrieved documents and previous outputs as untrusted data. No role has browsing, arbitrary function, storage or approval capabilities.

Both lesson and quiz coordination permit only the original candidate plus **two revisions in total**, shared across deterministic and model-review failures. Model or retrieval errors are explicit, not replacement content. A lesson run is saved once as an immutable final `Ready`, `Rejected` or `Failed` record. Caller cancellation propagates. Persistence failure never returns a successful record.

Quiz drafts require an existing accepted presenter-owned lesson. Each new draft receives an unpredictable server ID, version 1, the lesson's unchanged sources and a server-computed `DomainRules.DraftHash`. No draft is saved before review, and quiz drafting never invokes publication.

## Approved publication

`QuizPublisherAgent.PublishAsync` constructs one request-local `ApprovedPublicationTool` capturing the immutable `ApprovedPublication`, authenticated `PublicationCaller` and injected MCP client. Its JSON schema accepts an empty object only. The LLM must invoke this tool; prose is never a receipt. Model-supplied IDs, hashes, versions, presenters and any other arguments are rejected.

The function-invoking client permits no parallel calls, rejects multiple function calls and stops immediately after the real tool executes. A request-local task caches the first MCP invocation, including failure, for idempotent reuse. There is no second narration inference after a successful side effect. Approval expiry, identity, payload binding and durable cross-request idempotency remain the official MCP client's and server's responsibility.

Cancellation or a transport timeout can make a remote side effect's outcome uncertain. Do not invent a receipt or create a new approval to retry it. Reconcile or retry through the same server-bound approval and its durable idempotency policy.

## Budgets and activity

`AgentExecutionLimits` bounds input size, retrieved-source size, output size, per-model time, per-retrieval time and whole-workflow time. Roles have explicit output-token limits. The provider disables automatic inference retries. Structured-generation roles cannot request tools; publication has one forced local tool, a small token budget and bounded function iterations.

Activities contain only fixed, generic summaries with the server correlation ID and timestamp. They do not contain chain-of-thought, raw model output, prompt text, document contents, tokens or exception details. Final lesson records contain the accumulated role trace even if a progress observer disconnects. Persistence success is reported only after the save and is not pre-written into that immutable record. Quiz activities are progress-only because `QuizDraft` has no activity field; the existing lesson is never overwritten to append quiz events.

## Verification and remaining gates

The `AgentRoleSafetyTests`, `AgentWorkflowCoordinationTests` and `AgentPublicationBoundaryTests` exercise real Agent Framework agents with explicitly labelled scripted chat clients, plus fake role interfaces and stores. They perform no network inference, Entra token acquisition, cloud deployment or MCP publication.

Production readiness still requires authorised runtime checks of the configured deployment's Responses/structured-output/tool support, identity role assignments, quotas, real curriculum retrieval, persistent-store behaviour, approval authentication and the official MCP transport. Compilation and network-free tests do not establish these cloud gates.
