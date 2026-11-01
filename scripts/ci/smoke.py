"""Production-mode startup on loopback only, with synthetic configuration and no egress."""

import json
import re
import time
from uuid import UUID

from common import Refusal, command, require

LIVENESS = """exec 3<>/dev/tcp/127.0.0.1/8080
printf 'GET /health/live HTTP/1.1\\r\\nHost: classroom.test\\r\\nConnection: close\\r\\n\\r\\n' >&3
IFS= read -r -t 3 status <&3
[[ "$status" == "HTTP/1.1 200 "* ]]
"""


def configuration(kind):
    require(kind in ("web", "mcp"), "Only the two production hosts may be smoke-tested.")
    # Construct obvious synthetic IDs; these are not any user's directory or account context.
    tenant, web, caller, audience, worker, presenter = (str(UUID(int=index)) for index in range(1, 7))
    identity = caller if kind == "web" else worker
    values = {
        "ASPNETCORE_ENVIRONMENT": "Production", "DOTNET_ENVIRONMENT": "Production",
        "DOTNET_EnableDiagnostics": "0",
        "Storage__TableServiceUri": "https://storage.invalid", "Storage__ManagedIdentityClientId": identity,
        "Quiz__PublicBaseUrl": "https://classroom.test", "AllowedHosts": "classroom.test",
        "Mcp__CallerClientId": caller,
    }
    if kind == "web":
        values.update({
            "ReverseProxy__UseFixedHttpsOrigin": "true",
            "AzureAd__TenantId": tenant, "AzureAd__ClientId": web,
            "AzureAd__ClientCredentials__0__SourceType": "SignedAssertionFromManagedIdentity",
            "AzureAd__ClientCredentials__0__ManagedIdentityClientId": identity,
            "AzureAd__ClientCredentials__0__TokenExchangeUrl": "api://AzureADTokenExchange/.default",
            "Presenter__AllowedObjectIds__0": presenter,
            "Mcp__Endpoint": "https://mcp.invalid/mcp", "Mcp__TokenScope": f"api://{audience}/.default",
            "Mcp__ManagedIdentityClientId": identity,
            "DataProtection__BlobUri": "https://storage.invalid/keys/ring.xml",
            "DataProtection__ManagedIdentityClientId": identity,
            "Foundry__Endpoint": "https://ci-offline.invalid.services.ai.azure.com/openai/v1/", "Foundry__Deployment": "ci-offline",
            "Foundry__ManagedIdentityClientId": identity,
        })
    else:
        values.update({"Mcp__TenantId": tenant, "Mcp__Audience": audience})
    return values


def startup(kind, image, *, seconds=90):
    arguments = [
        "docker", "create", "--pull", "never", "--network", "none", "--read-only",
        "--cap-drop", "ALL", "--security-opt", "no-new-privileges=true", "--pids-limit", "256",
        "--memory", "768m", "--cpus", "2", "--log-driver", "none",
    ]
    for key, value in configuration(kind).items():
        arguments += ["--env", f"{key}={value}"]
    container_id = command(arguments + [image]).strip()
    require(re.fullmatch(r"[0-9a-f]{64}", container_id), "Smoke container did not return an exact container ID.")
    try:
        command(["docker", "start", container_id])
        user = command(["docker", "exec", container_id, "id", "-u"]).strip()
        require(user.isdecimal() and int(user) > 0, "The runtime process must use the image's non-root user.")
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            state = json.loads(command(["docker", "inspect", "--format", "{{json .State}}", container_id], timeout=10))
            require(state.get("Running") is True, "The production process exited before liveness passed.")
            try:
                command(["docker", "exec", container_id, "bash", "-e", "-c", LIVENESS], timeout=10)
                print(f"lumero-{kind}: native AMD64, non-root production startup and loopback 8080 liveness passed.")
                return
            except Refusal:
                time.sleep(min(1, max(0, deadline - time.monotonic())))
        require(False, "Network-isolated production startup did not pass liveness within its bound.")
    finally:
        # This removes only this job's isolated test container, never Azure resources or images.
        command(["docker", "rm", "--force", container_id], timeout=30)
