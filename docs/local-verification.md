# Local verification

## Scope

The local harness substitutes synthetic model-role outputs and synthetic presenter sign-in in **test assemblies only**. It retains the production application-owned workflow, safe HTTP pipeline, immutable state, grading, antiforgery, official MCP client, official MCP server and JWT validation. Web and MCP run in separate loopback Kestrel hosts. Azure Table operations use the actual local Azurite service.

This establishes local application behaviour. It does not prove live Entra federation, managed-identity permissions, Foundry model quality, quota, service-managed hosting, public ingress or deployment. No deployment authentication bypass exists.

The test data labels lessons and quizzes as local verification. Test signing keys and principal identifiers are synthetic. Do not replace them with an actual user's tokens or personal information.

## Prerequisites

- .NET SDK selected by `global.json`.
- Node.js supported by the pinned Azurite package.
- Free loopback ports `11002` for Table storage and, by default, `5180` for the browser harness. The test MCP host selects an available loopback port.
- Python Playwright and its Chromium browser for browser checks. The installed verification environment has Python Playwright 1.58.0 and Chromium 145.0.7632.6. This is separate from the centrally pinned .NET Playwright package.

Docker is not required for this harness. The verification machine has a Docker CLI without an available daemon, so the Node Table emulator is used.

## Restore, build and start storage

From the repository root:

```powershell
dotnet restore LessonPlanner.slnx --locked-mode
dotnet build LessonPlanner.slnx --no-restore
npm ci
npm run emulator
```

Azurite binds only to `127.0.0.1:11002`, disables telemetry and persists files beneath ignored `.local\azurite`. Leave it running for the commands below. Do not start another emulator on the same port or point these tests at Azure. Fixtures create unpredictable test table names and delete only the tables they create.

### Emulator dependency limitation

Azurite is pinned to 3.36.0 because 3.37.0 could not resolve its declared `applicationinsights@^3.16.0` dependency in the available registry. Compatible dependency fixes were applied. The recorded audit still reports **21 findings: 13 moderate, 7 high and 1 critical**. This is not a clean dependency audit.

Run `npm audit` before using the harness in a new environment, because advisory data can change. Keep this emulator loopback-only, use synthetic records, stop it after verification and do not include it in production images. Review a compatible update when the required upstream versions are available. Do not disable auditing or expose the service publicly to work around this limitation.

## Test commands

```powershell
dotnet test tests\LessonPlanner.UnitTests --no-build `
  --logger "trx;LogFileName=unit.trx" --results-directory .local\test-results

dotnet test tests\LessonPlanner.IntegrationTests --no-build `
  --logger "trx;LogFileName=integration.trx" --results-directory .local\test-results
```

The integration tests need the local Table emulator. They fail when it is unavailable rather than silently substituting memory state. Focused filters are useful during development:

```powershell
dotnet test tests\LessonPlanner.IntegrationTests --no-build `
  --filter "FullyQualifiedName~McpTransportTests|FullyQualifiedName~WebSecurityTests"
```

Coverage includes immutable drafts, provenance, conditional transactions, approval identity and expiry, concurrent publication, anonymous attempt budgets, retry conflicts, actual MCP token validation, protected participant tickets, key-provider restart and bounded payloads. A transport fault test drops a response after publication commits, then verifies that an official-client retry returns the one durable outcome.

Fresh storage clients and Web/key-provider instances test persistence across application replacement. Those tests must not be described as an Azurite process restart or an Azure disaster-recovery test.

## Browser harness

In another terminal:

```powershell
dotnet run --project tests\LessonPlanner.LocalHarness --no-build -- --port 5180
```

Open `http://127.0.0.1:5180/_local`. That landing page explains the synthetic identity/model boundary and links to the local presenter sign-in. The production `/auth/sign-in` route still belongs to the real authentication configuration.

Run one complete classroom sequence: request a lesson, read sources and review, download the document, generate and inspect exactly five questions, acknowledge the immutable draft, publish it, open the canonical quiz as an anonymous participant and submit five answers. Publication invokes real local MCP and commits real local Table state. Correct options must be absent from the participant's pre-submission HTML and response models.

Capture desktop and narrow-screen pages, form validation, keyboard focus, document/QR files and test output. Keep screenshots and transcripts in ignored `.local` state or an external evidence directory. Label every local capture as synthetic verification. Do not imply that a loopback QR address can be used by pupils on other devices.

The harness uses a real-time clock for interactive work. Automated tests use a controllable clock to exercise expiry without waiting. New harness runs allocate fresh test tables.

## Shutdown and cleanup

Ctrl+C requests graceful harness shutdown. An automation client can instead use this loopback-only test endpoint, with no Origin header:

```powershell
Invoke-WebRequest -Method Post -Uri http://127.0.0.1:5180/_local/stop `
  -Headers @{ 'X-Lumero-Local-Control' = 'stop' }
```

Graceful shutdown disposes Web and MCP and removes that harness's own table and key directory. If the process is killed, those records may remain in the local emulator; they are synthetic, ignored state, not production records. Stop the emulator separately after all dependent tests finish. Its persisted directory is retained for explicit, scoped cleanup.

Only stop processes that belong to this verification run. Do not kill unrelated .NET, Node or browser processes by name.
