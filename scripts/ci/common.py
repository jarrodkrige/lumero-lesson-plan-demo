"""Fail-closed guards shared by the manually approved delivery jobs."""

import json
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.request import HTTPRedirectHandler, ProxyHandler, build_opener

REPOSITORY = "jarrodkrige/lumero-lesson-plan-demo"
BRANCH = "jarrodkrige-microsoft-lesson-planner-implementation"
REF = f"refs/heads/{BRANCH}"
SHA = re.compile(r"[0-9a-f]{40}")
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")
GUID = re.compile(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}")
APP_ID = re.compile(
    r"/subscriptions/([^/]+)/resourceGroups/([A-Za-z0-9_.()-]+)/"
    r"providers/Microsoft\.App/containerApps/([a-z][a-z0-9-]{0,30}[a-z0-9])",
    re.IGNORECASE,
)
ROOT = Path(__file__).resolve().parents[2]
STATE = ROOT / "scripts" / "ci" / ".state"


class Refusal(Exception):
    """Only public-safe, constant diagnostics belong in this exception."""


def require(condition, message):
    if not condition:
        raise Refusal(message)


def command(arguments, *, timeout=60, stdin=None):
    try:
        result = subprocess.run(
            arguments, cwd=ROOT, input=stdin, capture_output=True,
            text=True, encoding="utf-8", timeout=timeout, check=False,
        )
    except (OSError, subprocess.SubprocessError):
        raise Refusal("External command could not complete; private diagnostics withheld.") from None
    require(result.returncode == 0, "External command failed; private diagnostics withheld.")
    return result.stdout


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):
        raise Refusal("Credential-bearing HTTP redirects are not permitted.")


def private_request(request):
    opener = build_opener(ProxyHandler({}), NoRedirect())
    with opener.open(request, timeout=30) as response:
        require(response.status == 200, "Private HTTP operation failed.")
        body = response.read(1024 * 1024 + 1)
    require(len(body) <= 1024 * 1024, "Private HTTP response exceeded its limit.")
    return json.loads(body)


def authorize(environment=None, head=None):
    env = os.environ if environment is None else environment
    source = env.get("SOURCE_SHA", "")
    require(SHA.fullmatch(source), "A full lowercase reviewed source SHA is required.")
    expected = {
        "GITHUB_ACTIONS": "true",
        "GITHUB_REPOSITORY": REPOSITORY,
        "GITHUB_EVENT_NAME": "workflow_dispatch",
        "GITHUB_REF": REF,
        "GITHUB_SHA": source,
        "GITHUB_WORKFLOW_SHA": source,
        "GITHUB_WORKFLOW_REF": f"{REPOSITORY}/.github/workflows/delivery.yml@{REF}",
        "RUNNER_ENVIRONMENT": "github-hosted",
        "RELEASE_CONFIRMATION": "release",
    }
    require(all(env.get(key) == value for key, value in expected.items()),
            "Delivery requires the approved repository, branch, workflow, source and explicit release confirmation.")
    require(env.get("RUNNER_DEBUG") != "1", "Delivery does not allow debug logging.")
    require(all(re.fullmatch(r"[1-9][0-9]*", env.get(key, ""))
                for key in ("GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT")),
            "An actual workflow run and attempt are required.")
    checked_out = command(["git", "rev-parse", "HEAD"]).strip() if head is None else head
    require(checked_out == source, "Checkout does not match the explicitly reviewed source.")
    return source


def registry(environment=None):
    env = os.environ if environment is None else environment
    server = env.get("ACR_LOGIN_SERVER", "")
    require(re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)?\.azurecr\.io", server),
            "An exact public-cloud ACR login-server secret is required, without scheme or path.")
    return server


def resource_id(value, subscription):
    match = APP_ID.fullmatch(value)
    require(match and GUID.fullmatch(subscription) and match[1].lower() == subscription.lower()
            and match[2] not in (".", "..") and len(match[2]) <= 90,
            "An exact app resource ID in the configured subscription is required.")
    return value


def prepare(kind):
    require(kind in ("build", "release"), "Only the approved build and release identities are supported.")
    authorize()
    registry()
    require(all(GUID.fullmatch(os.environ.get(key, "")) and int(os.environ[key].replace("-", ""), 16) != 0
                for key in ("AZURE_CLIENT_ID", "AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID")),
            "Federated identity environment secrets are missing or invalid.")
    if kind == "release":
        apps = [resource_id(os.environ.get(key, ""), os.environ["AZURE_SUBSCRIPTION_ID"])
                for key in ("WEB_CONTAINER_APP_ID", "MCP_CONTAINER_APP_ID")]
        require(apps[0].lower() != apps[1].lower(), "The two app resource IDs must be different.")
        selected_digests()
    STATE.mkdir(mode=0o700, parents=True, exist_ok=True)
    for key, name in (("AZURE_CONFIG_DIR", "azure"), ("DOCKER_CONFIG", "docker")):
        path = STATE / name
        require(os.environ.get(key) == str(path), "Tool state must remain in the isolated runner CI state directory.")
        path.mkdir(mode=0o700, exist_ok=True)
        path.chmod(0o700)
    print("Approved source and private configuration shape checked; no cloud access performed.")


def selected_digests(environment=None):
    env = os.environ if environment is None else environment
    digests = {app: env.get(f"{app.upper()}_DIGEST", "") for app in ("web", "mcp")}
    require(all(DIGEST.fullmatch(value) for value in digests.values()),
            "Both images must be immutable sha256 digests from this run's successful build job.")
    return digests


def record(stage, source, app=None, digest=None):
    require(stage in ("authorized source", "pushed", "selected", "verified"), "Invalid evidence stage.")
    require(SHA.fullmatch(source), "Invalid evidence source SHA.")
    line = f"{stage}: source `{source}`"
    if app is not None:
        require(app in ("web", "mcp") and DIGEST.fullmatch(digest or ""), "Invalid image evidence.")
        line += f", lumero-{app} `{digest}`"
    print(line)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
            summary.write(f"- {line}\n")
    if stage == "pushed" and os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as outputs:
            outputs.write(f"{app}_digest={digest}\n")


def main_entry(action):
    try:
        action()
    except Refusal as error:
        print(f"Delivery refused: {error}", file=sys.stderr)
        return 1
    except Exception:
        # Provider responses and subprocess exceptions can contain private identifiers.
        print("Delivery failed; private diagnostics withheld. No rollback or resource cleanup was attempted.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    def main():
        require(len(sys.argv) == 2, "A CI guard command is required.")
        if sys.argv[1] == "authorize":
            record("authorized source", authorize())
        elif sys.argv[1] in ("prepare-build", "prepare-release"):
            prepare(sys.argv[1].removeprefix("prepare-"))
        else:
            raise Refusal("Unknown CI guard command.")
    sys.exit(main_entry(main))
