# CI and reviewed image delivery

These files prepare CI; they do not establish that CI, federation, RBAC, images or
live acceptance have passed. They never bootstrap infrastructure, identities,
roles, Entra applications or repository settings. Aspire remains local. Use the
[identity bootstrap contract](../../docs/identity-bootstrap.md) for presenter,
MCP and GitHub identity mappings and bootstrap/protection procedures; the settings
below are CI prerequisites, not another bootstrap procedure.

## Unprivileged validation

`validate.yml` runs on push/PR and as a reusable workflow, with only
`contents: read`, no environment secrets and no OIDC permission. It performs:

- SDK installation from the existing `global.json`, locked solution restore from
  `https://packagefeedproxy.microsoft.io/nuget/v3/index.json`, and Release build.
- Application unit tests, isolated Foundry-host probe tests, and only the filtered
  `LessonEndpointCancellationTests` from the integration project after build.
  Those thirteen handler-only cases need no Azurite, real identities or models.
  All three TRX files (`unit.trx`, `probe.trx`, `handler-cancellation.trx`) must
  contain actual executed, passing cases; missing/empty/skipped/error results fail.
  The whole integration suite, browser, storage-emulator and model tests are not run.
- Mocked CI-helper tests, refusing zero discovered cases.
- Production Docker builds and startup checks on **native Linux AMD64**
  `ubuntu-24.04`. Both the runner CPU and Docker daemon must be AMD64. The default
  native builder targets `linux/amd64`; no QEMU, RID, dependency upgrades,
  additional feeds, build credentials or build arguments are introduced.
- Image architecture, numeric non-root user, production environment, fixed .NET
  entrypoint and port 8080 checks. Each image starts with synthetic configuration,
  `--network none`, no published ports/mounts, read-only filesystem, dropped
  capabilities and no new privileges. Only loopback `/health/live` is requested.
  `/health/ready`, auth and model endpoints are not invoked. Production key-ring
  initialisation can attempt dependency access; network isolation prevents cloud
  access. The synthetic quiz origin uses reserved `.test`, since production policy
  rejects `.invalid` and `.example` origins. Production validation is unchanged.
  This is startup evidence, **not** storage/auth/model acceptance.

Each isolated container is created before it is started, retaining its exact ID
for cleanup if startup fails. Only that job's own containers are removed. Azure
resources are never cleaned up automatically. Docker uses the root
`.dockerignore` production allowlist: exactly one `src/*` must follow the sole
`!src/` and precede every production child inclusion. Missing, misplaced or
duplicated boundary rules, reopened exclusions and per-Dockerfile ignore overrides
fail validation. No test/source archives or image artefacts are uploaded.

## Operator and repository settings

`delivery.yml` is **manual only**. Dispatch the reviewed commit on
`jarrodkrige-microsoft-lesson-planner-implementation` with:

- `source_sha`: the full lowercase 40-character reviewed commit SHA.
- `confirmation`: exactly `release`.

The event SHA, workflow SHA and checked-out HEAD must all equal that input. The
repository, branch, dispatch event and hosted runner are checked again in each
privileged stage. Debug logging is refused. Validation precedes the separately
approved build and release jobs. A repository-wide workflow concurrency group
serialises delivery runs; it does not lock out external Azure writers.

**Configure these settings separately before permitting delivery:**

- Environments exactly `lumero-build` and `lumero-release`, each restricted to the
  exact approved branch and requiring reviewer `@jarrodkrige`. Do not enable
  prevent-self-review; no additional reviewer is approved.
- Distinct build/release UAMIs. Their GitHub federated issuer is
  `https://token.actions.githubusercontent.com`, audience
  `api://AzureADTokenExchange`, and subjects are exactly:
  - `repo:jarrodkrige/lumero-lesson-plan-demo:environment:lumero-build`
  - `repo:jarrodkrige/lumero-lesson-plan-demo:environment:lumero-release`
- Build: only `AcrPush`, scoped to the classic-RBAC Basic registry with admin
  credentials disabled. Classic `AcrPush` is registry-wide; the scripts, not that
  role, restrict writes to `lumero-web` and `lumero-mcp`.
- Release: a custom role scoped to each existing app, containing only
  `Microsoft.App/containerApps/read`, `Microsoft.App/containerApps/write`, and
  `Microsoft.App/containerApps/revisions/read`. No managed-environment/RG reads,
  secret listing, revision activation, deployment or additional role grants.

Each privileged job has only `contents: read` and `id-token: write`. No privileged
PR/fork jobs, schedules, reusable delivery entry point or auto-release exist.

**Initial publication:** GitHub requires the dispatch workflow to exist on its
default branch before dispatch is available. The approved publication plan
initialises `main` and `jarrodkrige-microsoft-lesson-planner-implementation` at the
same validated, privacy-reviewed commit, retaining `main` as default. Delivery
must still be dispatched on the implementation branch, never `main`. Publication
is a separate operator action, not a completed CI step. These workflows do not
publish source, change repository settings or alter the separate live repository.

## Encrypted environment secrets

Set these **in both environments**, never repository variables, workflow inputs,
public files, logs or artefacts:

- `AZURE_CLIENT_ID`: that environment's distinct federated UAMI client ID.
- `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`.
- `ACR_LOGIN_SERVER`: exact public-cloud login server, without scheme or path.

Release additionally requires `WEB_CONTAINER_APP_ID` and `MCP_CONTAINER_APP_ID`:
exact, distinct existing app resource IDs in the configured subscription. No
resource names, topology, registry discovery or IDs are inferred.

OIDC acquisition and CLI login run only in future approved jobs. `auth.py` checks
issuer, subject, audience and workflow claims before captured CLI sign-in; Entra,
not this claim comparison, verifies the signed assertion. ACR uses its documented
OAuth data-plane exchange and Docker stdin login, not `az acr build`, admin
credentials or management-plane registry discovery. Raw provider/CLI errors are
withheld; Azure CLI logging/telemetry are disabled. Tool-managed credential state
stays in excluded `scripts/ci/.state/` on ephemeral runners; never upload it.

## Release and failure contract

Build first recreates and smoke-tests the source-bound images without Azure
configuration. Only after source/configuration rechecks does it authenticate and
push unique source/run/attempt tags. It verifies actual immutable manifest digests
against the validated local image configuration. Only those digests become
`web_digest`/`mcp_digest` outputs, with safe source/digest summaries for the separate
release approval. Freeform registry/image/tag/digest release inputs are absent.

The release job preflights **both** existing apps before any PATCH. Required state:

- Stable provisioned app with latest revision already ready; `Single` revision
  mode; exactly one named container and one assigned workload UAMI.
- Exactly the `Consumption` workload profile, `minReplicas=maxReplicas=1`, and
  no scaling rules. Web remains **1 CPU/2Gi**; MCP remains **0.25 CPU/0.5Gi**;
  no GPUs. Missing, malformed or out-of-scope profile/scale/resources fail before
  any mutation. All these constraints are rechecked immediately before each PATCH.
- Correct registry/repository with an existing `sha256` image. Registry pulls use
  that workload UAMI, not username/password. Foundation/bootstrap is separate.
- HTTPS-only ingress targeting 8080: Web external, MCP internal. Traffic is absent
  or wholly on the latest revision. No init containers, enabled Dapr, service
  bindings or fixed revision suffix.
- Exactly one **TCP Startup on 8080**, HTTP Liveness `/health/live`, and HTTP
  Readiness `/health/ready` probe on 8080, matching `infra/workloads.bicep`.
  HTTP Startup, missing/duplicate probes and mixed handlers are refused; existing
  timing and HTTP Host headers are preserved. No Development/Testing overrides
  or secret/certificate/key/connection-string credential configuration.
- Web retains its sole `SignedAssertionFromManagedIdentity` credential. These
  noncredential auth settings must be inspectable directly in app GET responses,
  not hidden behind secret references: Web `AzureAd__ClientId`,
  `AzureAd__TenantId`, `Mcp__TokenScope`, `Mcp__CallerClientId`; MCP `Mcp__Audience`,
  `Mcp__TenantId`, `Mcp__CallerClientId`. App audiences are distinct, tenants/callers
  agree, and Web scope is exactly `api://<MCP audience>/.default`.

Only direct ARM API `2025-01-01` app GET/image merge PATCH/revision GET are used.
The full single container is copied with only its image replaced; all omitted
configuration/template settings are preserved by merge PATCH. The scripts
compare non-image settings before and after, update **MCP then Web**, and avoid
creating a revision when the digest already matches. No async-operation endpoint
or additional resource API is followed.

Each app has a 600-second readiness bound. Success requires the exact desired
image/template, latest-ready revision, and active `Provisioned`/`Healthy`/`Running`
revision. Failed, unhealthy, stopped, unknown/incompatible or drifted state fails
closed or reaches the bound; scale-to-zero is not accepted as running evidence.
Readiness is not live acceptance and does not invoke a model.

Delivery has no paid-window or budget gate and requires no operating-date secrets.
Explicit manual dispatch, both human environment approvals and all technical
checks still apply. Private HTTP retains a 30-second socket timeout and 1 MiB
response limit. Subprocesses default to 60 seconds; cloud/account selection uses
30 seconds, each Docker build 900 seconds and each push 600 seconds. ARM requests
are capped at 60 seconds, shortened for the remaining per-app readiness bound;
readiness sleeps are at most 5 seconds. Job timeouts remain 3 minutes for source
authorisation, 45 for validation/build and 30 for release.

**Already accepted service operations cannot be revoked by a client timeout.**
Pushes and two-app updates are non-atomic: a later failure can leave an earlier
image pushed/app changed, or an accepted operation progressing. Stop and
investigate privately; no automatic rollback, retry of mutations, activation or
resource deletion occurs.

The documented PATCH API offers no verified conditional `If-Match` contract.
Pre/post checks cannot eliminate an external write between GET and PATCH; arrange
exclusive configuration-change coordination. Do not widen RBAC to bypass a
failure or claim transactional preservation.

## Local checks and references

From the repository root, the safe helper-only check is:

```powershell
python -B scripts\ci\test_delivery.py
```

All subprocess/network operations in that suite are mocked and unexpected ones
fail. It covers authorised preparation/push/release without operating-date secrets,
retained operation/readiness bounds, probe protocols, approved sizing/scale/profile
before mutation, Docker context ordering, and all three required TRX results. It
does not run .NET, Docker, Azure login, services or inference. CI application case
totals belong to their actual source/run, not historical checkpoints.

The handler-only CI selection, after a Release build, is:

```powershell
dotnet test tests\LessonPlanner.IntegrationTests\LessonPlanner.IntegrationTests.csproj `
  --no-build --no-restore --configuration Release `
  --filter "FullyQualifiedName~LessonEndpointCancellationTests" `
  --logger "trx;LogFileName=handler-cancellation.trx" --results-directory scripts\ci\.state\test-results
```

Do not replace that filter with an unfiltered integration/browser run. Native
AMD64 startup remains a future CI check unless separately executed on native AMD64.

Actions are full-SHA pinned; official `actions/checkout` v4 and
`actions/setup-dotnet` v5 commit objects were checked on 2026-11-01. Contracts:
[GitHub OIDC](https://docs.github.com/en/actions/reference/security/oidc),
[ACR OAuth](https://github.com/Azure/acr/blob/main/docs/AAD-OAuth.md),
[Container Apps merge PATCH](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/container-apps/update?view=rest-resource-manager-containerapps-2025-01-01),
[revision GET](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/container-apps-revisions/get-revision?view=rest-resource-manager-containerapps-2025-01-01),
[Azure CLI configuration](https://learn.microsoft.com/en-us/cli/azure/azure-cli-configuration),
and [.NET Docker builds](https://github.com/dotnet/dotnet-docker/blob/main/documentation/building-docker-images.md).
