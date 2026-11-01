# Lumero lesson planner

A .NET 10 reference application for a secondary computing teacher workshop. A presenter requests a lesson, examines its curriculum sources and review, downloads an editable Word document, then approves publication of a five-question quiz. Participants answer anonymously through server-rendered pages.

**Verification status:** deployed application source `33e9f3d6066b6e130b873800f79490591341fc12` completed the recorded live acceptance on 1 November 2026. It covered personal Entra presenter sign-in, one genuine OCR J277 sorting lesson, its actual DOCX, a reviewed five-question draft, presenter-approved MCP publication, the canonical pupil link and decoded QR, and an anonymous submission correctly marked 3/5. The source-bound protected CI passed 512 .NET cases and 68 CI helper cases. See [recorded live acceptance](docs/local-verification.md#recorded-live-acceptance) for evidence boundaries and content-quality limitations.

**Historical local checkpoint:** `ea5e365` passed 361 application unit tests, 91 integration tests, 29 isolated hosting-probe tests and 40 browser checks, with a warning-free solution build. Local tests used actual Table persistence and authenticated official MCP transport with explicitly synthetic model and identity services. The subsequent 210-second Generate amendment passed thirteen dedicated cancellation/error-path cases; that focused result is not a fresh full-suite or browser checkpoint. These historical synthetic captures remain distinct from the later live acceptance.

Independent teacher-device/network access, production restart/replay/load behaviour and service-managed Foundry hosting were not established by the live acceptance. Further cloud execution requires explicit operational approval and technical validation. There is no production model fallback or authentication bypass.

## Application boundary

- **Web:** Blazor static server-side rendering, single-tenant presenter access, native forms and antiforgery protection. Participant pages require neither an account nor JavaScript.
- **Agents:** separate Orchestrator, Curriculum, Lesson Writer, Reviewer and Quiz Publisher roles using Microsoft Agent Framework. Application-owned coordination enforces typed outputs, independent validation and at most two revisions.
- **Retrieval:** curated HTTPS sources, controlled redirects and network addresses, bounded payloads and provenance. Source material is treated as untrusted reference content.
- **Publication:** a presenter approves an immutable server-owned draft. The Quiz Publisher invokes `publish_quiz` through the official C# MCP client over authenticated stateless Streamable HTTP to a separate service.
- **State:** Azure Table transactions atomically consume approval and create publication. ETags, idempotency and attempt limits apply on the server. Unpublished drafts have no public quiz.
- **Participation:** unpredictable identifiers, short-lived protected submission tickets, no participant names or email addresses, server grading and durable results. Correct-option identifiers and explanations are excluded from pre-submission participant models.
- **Artifacts:** Open XML SDK creates editable DOCX files. QRCoder encodes the canonical quiz URL locally after the Web service verifies the page is reachable. No email or external QR service is used.

## Build and unit tests

Install the SDK selected by `global.json` and run from the repository root:

```powershell
dotnet restore LessonPlanner.slnx --locked-mode
dotnet build LessonPlanner.slnx --no-restore
dotnet test tests\LessonPlanner.UnitTests --no-build
```

Direct package versions are centralised in `Directory.Packages.props`; NuGet lock files record transitive versions. Auditing and warnings-as-errors remain enabled. Configure an organisation-approved package mirror locally if the public feed is unavailable. Do not commit machine-specific feed credentials or disable auditing to complete a restore.

## Run a local verification session

The isolated harness uses synthetic presenter identities and model outputs from the **test assemblies**. It exercises the production coordination, persistence, HTTP, antiforgery, grading and official MCP transport. It makes no inference calls and is not a deployment mode.

```powershell
npm ci
npm run emulator
```

Keep that terminal open. In another terminal:

```powershell
dotnet run --project tests\LessonPlanner.LocalHarness -- --port 5180
```

Open `http://127.0.0.1:5180/_local`. The harness binds to loopback only. Read the disclosure before using its synthetic sign-in link. Ctrl+C stops the harness and removes only its own test table and key directory. The emulator's storage directory remains on disk.

The pinned Node emulator has unresolved transitive audit findings and must remain local-only. See [local verification](docs/local-verification.md) for exact limitations, integration commands, cleanup and evidence guidance.

## Configure the real application

Replace the explicit placeholders using deployment environment variables or local user secrets. The production executables fail closed when required identity configuration is absent. Their `Development` configuration does not add a synthetic sign-in or model.

The production boundary requires an existing approved Foundry model endpoint, separate Web and MCP managed identities, an MCP API application role, a presenter allowlist, Azure Table state and a private Blob-backed data-protection key ring. The Web confidential client uses a managed-identity federated assertion. See the [stack and integration contract](docs/stack-contract.md) and [configuration guide](docs/configuration.md).

Both containers use port `8080`. MCP is available at `/mcp`; health endpoints are `/health/live` and `/health/ready`. Production Table creation and identity assignments are infrastructure prerequisites, not application startup side effects. The [standalone Bicep infrastructure](infra/README.md) separates foundation provisioning from the gated production workloads. The [identity bootstrap contract](docs/identity-bootstrap.md) preserves the separate presenter, MCP, build and release identities. Production Dockerfiles publish only their required project graphs with locked dependencies and non-root runtime images. Compiled templates and built images do not establish live readiness.

The [reproducible checkpoint manifest](docs/checkpoint-manifest.json) records the verified code revision, dependency locks, exact commands, measured results and SHA-256 hashes for the retained workshop evidence. Synthetic runtime captures and raw test output are kept outside the public repository.

The implemented **application-hosted** inference path was used for the recorded live acceptance. `AsAIAgent` does not register a service-managed Foundry agent. The [isolated hosting probe](probes/LessonPlanner.FoundryHostProbe/README.md) compiles the pinned builder-level Foundry Responses v2.0 API. Its default `--check` execution makes no network call. Service-managed registration, hosting and model invocation through that separate probe remain unverified and require their own explicit Azure approval.

## Workshop code

[Original C# examples](examples/README.md) demonstrate objects, interfaces, constructor injection and a grading-policy change without cloud access. Typed request inputs cover OCR J277 sorting and a robot sports day activity. The examples are deliberately isolated from publishable production drafts.

## Source layout

- `src\LessonPlanner.Core`: typed contracts, deterministic rules and grading.
- `src\LessonPlanner.Agents`: role adapters, Foundry provider and bounded coordination.
- `src\LessonPlanner.Infrastructure`: retrieval, state, official MCP client, document and QR rendering.
- `src\LessonPlanner.Web`: presenter and participant HTTP application.
- `src\LessonPlanner.Mcp`: independently authenticated publication service.
- `src\LessonPlanner.AppHost` and `ServiceDefaults`: Aspire composition and service conventions.
- `probes\LessonPlanner.FoundryHostProbe`: compile-only by default, separately gated hosting experiment.
- `tests`: focused unit, real local integration, isolated probe tests and reproducible browser-harness code.
- `infra`: parameterised Bicep foundation and gated production workloads.
- `.github\workflows` and `scripts\ci`: unprivileged validation and protected, bounded image delivery.

Do not submit secrets, participant personal information, private curriculum reference decks or machine-specific cloud identifiers to this public repository.
