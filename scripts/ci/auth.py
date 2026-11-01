"""Future runner-only OIDC login; raw provider errors and tokens never reach logs."""

import base64
import json
import os
import sys
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit
from urllib.request import Request

from common import REF, REPOSITORY, authorize, command, main_entry, prepare, private_request, require

AUDIENCE = "api://AzureADTokenExchange"
ISSUER = "https://token.actions.githubusercontent.com"


def validate_claims(claims, environment, source):
    expected = {
        "iss": ISSUER, "aud": AUDIENCE,
        "sub": f"repo:jarrodkrige@10612771/lumero-lesson-plan-demo@1441161999:environment:{environment}",
        "repository": REPOSITORY, "ref": REF, "sha": source,
        "workflow_ref": f"{REPOSITORY}/.github/workflows/delivery.yml@{REF}",
        "workflow_sha": source, "event_name": "workflow_dispatch", "runner_environment": "github-hosted",
    }
    require(all(claims.get(key) == value for key, value in expected.items()),
            "The issued OIDC claims do not match the approved environment, source and audience.")


def login(kind):
    prepare(kind)
    source = authorize()
    environment = f"lumero-{kind}"
    endpoint = urlsplit(os.environ["ACTIONS_ID_TOKEN_REQUEST_URL"])
    require(endpoint.scheme == "https" and endpoint.hostname
            and endpoint.hostname.endswith(".actions.githubusercontent.com")
            and endpoint.port in (None, 443) and endpoint.username is None and endpoint.password is None
            and not endpoint.fragment, "Unexpected runner OIDC endpoint; no credentials sent.")
    query = [(key, value) for key, value in parse_qsl(endpoint.query) if key != "audience"]
    query.append(("audience", AUDIENCE))
    url = urlunsplit((endpoint.scheme, endpoint.netloc, endpoint.path, urlencode(query), ""))
    request = Request(url, headers={"Authorization": "bearer " + os.environ["ACTIONS_ID_TOKEN_REQUEST_TOKEN"]})
    token = private_request(request)["value"]
    require(isinstance(token, str) and len(token.split(".")) == 3, "The runner did not issue an OIDC token.")
    payload = token.split(".")[1]
    claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
    # Claim comparison is a guard, not signature verification; Entra validates the signed assertion.
    validate_claims(claims, environment, source)
    command(["az", "cloud", "set", "--name", "AzureCloud", "--only-show-errors"], timeout=30)
    command([
        "az", "login", "--service-principal", "--username", os.environ["AZURE_CLIENT_ID"],
        "--tenant", os.environ["AZURE_TENANT_ID"], "--federated-token", token,
        "--output", "none", "--only-show-errors",
    ], timeout=60)
    command(["az", "account", "set", "--subscription", os.environ["AZURE_SUBSCRIPTION_ID"], "--only-show-errors"],
            timeout=30)
    print("Federated identity login completed; provider output and token material were withheld.")


if __name__ == "__main__":
    def main():
        require(len(sys.argv) == 2 and sys.argv[1] in ("build", "release"), "Choose the build or release identity.")
        login(sys.argv[1])
    sys.exit(main_entry(main))
