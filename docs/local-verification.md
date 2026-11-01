# Local verification

## Scope

The local harness substitutes synthetic model-role outputs and synthetic presenter sign-in in **test assemblies only**. It retains the production application-owned workflow, safe HTTP pipeline, immutable state, grading, antiforgery, official MCP client, official MCP server and JWT validation. Web and MCP run in separate loopback Kestrel hosts. Azure Table operations use the actual local Azurite service.

This establishes local application behaviour. It does not prove live Entra federation, managed-identity permissions, Foundry model quality, quota, service-managed hosting, public ingress or deployment. No deployment authentication bypass exists.

The test data labels lessons and quizzes as local verification. Test signing keys and principal identifiers are synthetic. Do not replace them with an actual user's tokens or personal information.

## Recorded checkpoint

The [checkpoint manifest](checkpoint-manifest.json) records the exact clean code revision, audited locked build, 361 application unit tests, 91 integration tests, 29 isolated probe tests, 40 browser checks, four safe probe CLI cases and eleven offline teaching-example cases. The 28 grading-policy example tests are included in the application unit total.

Its evidence entries identify nineteen retained files by relative name, byte length and SHA-256: nine screenshots, a downloaded DOCX, a locally encoded QR, the browser report and seven build/test records. Raw runner output can contain machine paths, so the bundle is retained separately with the implementation session. No cookies, protected tickets or raw HTML were captured by the browser script. The manifest identifies the tested code commit rather than inventing a self-referential documentation commit.

Use the commands below against that code revision to reproduce the checks. New runtime identifiers and timestamps produce different capture hashes. The recorded QR belongs to a stopped loopback harness and is not the workshop's public quiz URL. The owned harness and emulator were stopped after verification.

## Recorded live acceptance

On 1 November 2026, [protected run 41323236587](https://github.com/jarrodkrige/lumero-lesson-plan-demo/actions/runs/41323236587) deployed application source `33e9f3d6066b6e130b873800f79490591341fc12`. Its source-bound CI passed 361 unit, 29 isolated probe and 122 selected HTTP integration cases, plus 68 CI helper cases. Independent post-release checks verified the new image digests and HTTP boundaries. Later documentation amendments do not change that deployed source or the historical local checkpoint above.

The approved live sequence established:

- Personal Entra sign-in and access to the protected presenter page on the corrected confidential-client configuration. Authentication values were not inspected or exported.
- One genuine application-hosted lesson generation for OCR J277 sorting. The actual sixty-minute plan, source provenance, three algorithm descriptions and assessment prompts were reviewed against specification section 2.1.3. The downloaded DOCX package and its extracted text were verified.
- One reviewed draft with exactly five MCQs. Every key and explanation was independently checked, including the complete first bubble-sort pass on `[5, 2, 8, 1]`, yielding `[2, 5, 1, 8]`. The actual draft identifier returned anonymous 404 before publication and its private page required sign-in.
- One presenter-approved publication through the deployed official MCP path. A later protected-page read confirmed publication. The genuine downloaded QR decoded to the exact canonical HTTPS pupil URL, which the anonymous participant route served successfully.
- One submission in a fresh unauthenticated browser context, deliberately answering three correctly and two incorrectly. The server returned 3/5 and correct feedback for all questions. The initial quiz view exposed no marking keys. A second fresh context retrieved the identical saved result through its private capability link, consistent with the displayed anyone-with-link warning.

### Content and evidence limitations

- All five correct answers occupied the first option. Marking was correct; answer positions were predictable. Several distractors were easy. This was an introductory formative demonstration, not a validated high-stakes assessment.
- The lesson supplies a teaching sequence and activity instructions. Teachers still need to prepare the trace tables, worksheets, algorithm summaries and code segment it references. It does not contain completed worked trace tables or a ready-made pupil worksheet pack.
- The output retains em dashes, a verbal "practice" requiring British-English "practise", and an OCR excerpt truncated at both ends. The full source URL and downloaded-payload hash remain available. Sorting-efficiency statements are introductory generalisations, not universal performance guarantees or a requirement to memorise formal complexity notation for section 2.1.3.
- Production warning-level logs did not provide a complete successful callback/generation/publication request audit. Accepted sign-in combines actual protected access with verified configuration and confidential-code-redemption regression tests. No actual client assertion or token-endpoint payload was captured. Publication evidence combines the verified source/configuration with the normal presenter action and subsequent durable application read paths; no direct Table-row export or successful MCP packet trace was retained.
- Shared model-deployment metrics cannot establish this attempt's exact provider-call count, tokens or bill. UI actions and role activity events are not provider request counts.
- The fresh-context result read does not prove production cold-restart recovery, replay/idempotency or load behaviour. Those scopes retain their separately described local tests. Independent teacher-device/network participation and a physical-device QR scan were not performed. Service-managed Foundry hosting was not used or established.

The real DOCX, reviewed lesson/quiz text, safe screenshots, QR, exact action times and integrity manifest are retained in the private acceptance evidence. The result capability URL, authentication values and environment-specific identifiers are not published here. Earlier rough timing notes are preserved with corrections from the exact UI timestamps; the private attempt record was written after the lesson click and is not represented as a pre-action receipt. The accepted publication expires on **2 November 2026 at 16:55 UTC**, at the precision displayed by the application. Do not assume that its saved link remains available after expiry. No further inference or publication was performed to address the recorded editorial observations.

## Prerequisites

- .NET SDK selected by `global.json`.
- Node.js supported by the pinned Azurite package.
- Free loopback ports `11002` for Table storage and, by default, `5180` for the browser harness. The test MCP host selects an available loopback port.
- Python with `venv` support. Browser verification uses a repository-local environment with exact packages in `tests\browser\requirements.txt`. The verified environment used Python 3.14.7, Python Playwright 1.58.0 and Chromium 145.0.7632.6. This is separate from the centrally pinned .NET Playwright package and does not modify a shared Python installation.

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

dotnet test tests\LessonPlanner.FoundryHostProbe.Tests --no-build `
  --logger "trx;LogFileName=foundry-probe.trx" --results-directory .local\test-results
```

The integration tests need the local Table emulator. They fail when it is unavailable rather than silently substituting memory state. Focused filters are useful during development:

```powershell
dotnet test tests\LessonPlanner.IntegrationTests --no-build `
  --filter "FullyQualifiedName~McpTransportTests|FullyQualifiedName~WebSecurityTests"
```

Coverage includes immutable drafts, provenance, conditional transactions, approval identity and expiry, concurrent publication, anonymous attempt budgets, retry conflicts, actual MCP token validation, protected participant tickets, key-provider restart and bounded payloads. A transport fault test drops a response after publication commits, then verifies that an official-client retry returns the one durable outcome. Native endpoint tests also reconcile an already committed publication when the publisher's final response fails or supplies an incorrect receipt, without repeating the action. Request cancellation remains honoured. Reachability failures preserve the publication link and withhold the QR.

### Offline HTTP boundary tests

Protected CI also runs the origin, shared anonymous rate, security-registration and handler-cancellation cases without an emulator, model or live credentials:

```powershell
dotnet test tests\LessonPlanner.IntegrationTests --no-restore --configuration Release `
  --filter "FullyQualifiedName~LessonEndpointCancellationTests|FullyQualifiedName~WebOriginTests|FullyQualifiedName~WebRateLimitTests|FullyQualifiedName~WebSecurityTests"
```

These cases exercise the Production-only fixed HTTPS scheme, exact canonical Host, ignored forwarding headers, the shared 600-request-per-minute partition and unchanged 32-request concurrency limit. The real OIDC challenge and logout handlers use static test-only metadata; default test credential and HTTP handlers reject external access. They verify exact HTTPS callback URIs without performing a live sign-in. A separate synthetic provider exercises the complete OIDC challenge, callback, real MSAL code redemption, signed ID-token validation and presenter cookie. It verifies the managed-identity credential selection, outgoing client assertion and PKCE, raw tenant/object claims, absence of tokens in the cookie, and the bounded isolated token cache. Negative cases reject an incorrect issuer, audience or nonce, tampered state, missing correlation or nonce cookies, and provider errors. Authenticated OIDC sign-out also verifies removal of the synthetic account's cached tokens. Failure-page tests verify safe support references, correlation logging and separation from authorisation denial. This test-only provider supplies synthetic assertions and tokens and has no network transport; it is not evidence of successful Azure managed-identity federation. Test-only ephemeral key storage does not alter Production persistence registration. Existing publication and grading flow tests still require Azurite.

Nested presenter and participant pages are checked against the real static asset host. Their stylesheet URLs are root-relative, so the strict `base-uri 'none'` policy remains in place without a blocked `<base>` element. The isolated probe tests use no real credentials or network calls.

Fresh storage clients and Web/key-provider instances test persistence across application replacement. Those tests must not be described as an Azurite process restart or an Azure disaster-recovery test.

## Browser harness

In another terminal:

```powershell
dotnet run --project tests\LessonPlanner.LocalHarness --no-build -- --port 5180
```

Open `http://127.0.0.1:5180/_local`. That landing page explains the synthetic identity/model boundary and links to the local presenter sign-in. The production `/auth/sign-in` route still belongs to the real authentication configuration.

Prepare the isolated browser dependencies, then run the automated sequence in another terminal:

```powershell
python -m venv .local\browser-venv
.local\browser-venv\Scripts\python.exe -m pip install -r tests\browser\requirements.txt
.local\browser-venv\Scripts\python.exe -m playwright install chromium
.local\browser-venv\Scripts\python.exe tests\browser\verify_classroom.py `
  --url http://127.0.0.1:5180 --output .local\browser-evidence
```

The script accepts only an explicit loopback origin, requires the exact local-verification notice and blocks unexpected external browser requests. It uses native headless Playwright. It does not save cookies, raw HTML, form tokens or protected tickets. Failure diagnostics omit query strings and replace unpredictable identifiers. It always closes its browser.

The sequence checks lesson generation, sources/review, an actual DOCX download, exactly five private questions, anonymous 404 before publication, unchecked required approval, real MCP publication, stable canonical URL and local QR decoding. It then uses an anonymous JavaScript-disabled mobile context to check required answers, the absence of private marking data and identity fields before submission, server grading, explanations after submission and durable result refresh. Keyboard skip navigation, labels, layout bounds, safe generated-text rendering and browser resource failures are also checked.

A successful run reports **40 checks**, creates **nine screenshots**, saves the DOCX and PNG QR, and writes `browser-verification.json`. The report records the source commit, tracked-change state, actual package/browser versions, check outcomes and SHA-256 hashes of the captured assets. Asset timestamps and unpredictable IDs change between runs; verify the recorded hashes rather than expecting byte-identical screenshots.

Keep captures and test output in ignored `.local` state or an external evidence directory. Every capture is synthetic verification. A loopback QR cannot be used by pupils on another device. These automated checks do not constitute a comprehensive accessibility audit or live model evaluation.

Start a fresh harness before each full browser attempt. The production model-action limiter is retained and permits six actions per ten minutes; a complete run uses four. Repeating it immediately against the same process can correctly receive HTTP 429. The harness uses a real-time clock for interactive work. Automated integration tests use a controllable clock for expiry. New harness runs allocate fresh test tables.

## Shutdown and cleanup

Ctrl+C requests graceful harness shutdown. An automation client can instead use this loopback-only test endpoint, with no Origin header:

```powershell
Invoke-WebRequest -Method Post -Uri http://127.0.0.1:5180/_local/stop `
  -Headers @{ 'X-Lumero-Local-Control' = 'stop' }
```

Graceful shutdown disposes Web and MCP and removes that harness's own table and key directory. If the process is killed, those records may remain in the local emulator; they are synthetic, ignored state, not production records. Stop the emulator separately after all dependent tests finish. Its persisted directory is retained for explicit, scoped cleanup.

Only stop processes that belong to this verification run. Do not kill unrelated .NET, Node or browser processes by name.
