"""Image-only ARM merge PATCHes; no infrastructure, secret reads or activation calls."""

from copy import deepcopy
import json
import os
import re
import sys
import time

from common import DIGEST, GUID, authorize, command, main_entry, record, registry, require, resource_id, selected_digests

API_VERSION = "2025-01-01"
ARM = "https://management.azure.com"
READ_ONLY_PROPERTIES = {
    "provisioningState", "runningStatus", "latestRevisionName", "latestReadyRevisionName",
    "latestRevisionFqdn", "customDomainVerificationId", "outboundIpAddresses", "eventStreamEndpoint",
}


def rest(app_id, method="GET", body=None, revision=None, timeout=60):
    resource_id(app_id, os.environ["AZURE_SUBSCRIPTION_ID"])
    require(method in ("GET", "PATCH") and (method == "PATCH") == (body is not None),
            "Only an app GET or image PATCH is permitted.")
    path = app_id
    if revision is not None:
        require(method == "GET" and re.fullmatch(r"[A-Za-z0-9-]{1,128}", revision),
                "Only an exact returned revision name may be read.")
        path += f"/revisions/{revision}"
    arguments = ["az", "rest", "--method", method, "--url", f"{ARM}{path}?api-version={API_VERSION}",
                 "--only-show-errors", "--output", "json" if method == "GET" else "none"]
    if body is not None:
        arguments += ["--headers", "Content-Type=application/json", "--body", json.dumps(body, separators=(",", ":"))]
    output = command(arguments, timeout=min(60, timeout))
    return json.loads(output) if method == "GET" else None


def container(app):
    containers = app.get("properties", {}).get("template", {}).get("containers", [])
    require(isinstance(containers, list) and len(containers) == 1 and isinstance(containers[0], dict),
            "Exactly one existing application container is required.")
    return containers[0]


def environment(app):
    values = container(app).get("env", [])
    require(isinstance(values, list), "Unexpected container environment configuration.")
    require(all(isinstance(value, dict) and isinstance(value.get("name"), str) for value in values),
            "Unexpected container environment entry.")
    result = {value["name"]: value.get("value") for value in values}
    require(len({key.replace(":", "__").lower() for key in result}) == len(values),
            "Duplicate or case-ambiguous environment keys are not supported.")
    return result


def validate_app(app, app_id, kind, server, *, require_ready=True):
    require(isinstance(app, dict) and app.get("id", "").lower() == app_id.lower()
            and app.get("type", "").lower() == "microsoft.app/containerapps"
            and isinstance(app.get("location"), str) and app["location"],
            "The existing app response did not match the configured resource.")
    properties = app.get("properties", {})
    if require_ready:
        require(properties.get("provisioningState") == "Succeeded" and properties.get("latestRevisionName")
                and properties.get("latestReadyRevisionName") == properties["latestRevisionName"],
                "An existing app must have a stable provisioned and ready latest revision.")
    require(kind in ("web", "mcp") and properties.get("workloadProfileName") == "Consumption",
            "Only the approved Web and MCP apps on the Consumption profile may be released.")
    template = properties.get("template", {})
    scale = template.get("scale")
    require(isinstance(scale, dict)
            and all(type(scale.get(key)) is int and scale[key] == 1 for key in ("minReplicas", "maxReplicas"))
            and (scale.get("rules") is None or scale["rules"] == []),
            "Existing scale must retain minReplicas=maxReplicas=1 without scaling rules.")
    require(not template.get("initContainers") and not template.get("revisionSuffix"),
            "Init containers and fixed revision suffixes are outside this image-only release contract.")
    config = properties.get("configuration", {})
    require(config.get("activeRevisionsMode") == "Single", "Release requires existing Single revision mode; it never activates revisions.")
    ingress = config.get("ingress", {})
    require(ingress.get("targetPort") == 8080 and ingress.get("external") is (kind == "web")
            and ingress.get("allowInsecure") is False,
            "Existing ingress must be HTTPS-only, port 8080, Web external and MCP internal.")
    require(not (config.get("dapr") or {}).get("enabled") and not template.get("serviceBinds"),
            "Unexpected sidecar or bound-service configuration.")
    traffic = ingress.get("traffic") or []
    require(not traffic or (len(traffic) == 1 and traffic[0].get("latestRevision") is True
                            and traffic[0].get("weight") == 100 and not traffic[0].get("revisionName")),
            "Pinned or split revision traffic is not supported.")
    identity = app.get("identity", {})
    identities = identity.get("userAssignedIdentities", {})
    require(identity.get("type") == "UserAssigned" and isinstance(identities, dict) and len(identities) == 1,
            "Exactly one existing user-assigned workload identity is required.")
    credentials = config.get("registries", [])
    require(isinstance(credentials, list) and len(credentials) == 1
            and credentials[0].get("server") == server
            and credentials[0].get("identity", "").lower() in {key.lower() for key in identities}
            and not credentials[0].get("username") and not credentials[0].get("passwordSecretRef"),
            "Registry pulls must already use the app's managed identity and exact configured registry.")
    current = container(app)
    require(isinstance(current.get("name"), str) and current["name"], "The existing container must be named.")
    resources = current.get("resources")
    cpu, memory = (1, "2Gi") if kind == "web" else (0.25, "0.5Gi")
    require(isinstance(resources, dict) and type(resources.get("cpu")) in (int, float)
            and resources["cpu"] == cpu and resources.get("memory") == memory and resources.get("gpu") in (None, 0),
            "Existing resources must remain Web 1 CPU/2Gi and MCP 0.25 CPU/0.5Gi, without GPUs.")
    prefix = f"{server}/lumero-{kind}@"
    require(current.get("image", "").startswith(prefix) and DIGEST.fullmatch(current["image"][len(prefix):]),
            "The existing image must already belong to the approved repository and use a digest; bootstrap is separate.")
    probes = current.get("probes", [])
    required_paths = {"Liveness": "/health/live", "Readiness": "/health/ready"}
    require(isinstance(probes, list) and len(probes) == 3
            and all(isinstance(probe, dict) and isinstance(probe.get("type"), str) for probe in probes)
            and {probe["type"] for probe in probes} == {"Startup", "Liveness", "Readiness"},
            "Exactly one TCP Startup and HTTP Liveness/Readiness probe are required.")
    for probe in probes:
        if probe["type"] == "Startup":
            tcp = probe.get("tcpSocket")
            require(isinstance(tcp, dict) and tcp.get("port") == 8080 and tcp.get("host") in (None, "")
                    and probe.get("httpGet") is None and probe.get("exec") is None,
                    "The existing Startup probe must use only the app's TCP port 8080.")
        else:
            http = probe.get("httpGet")
            require(isinstance(http, dict) and http.get("path") == required_paths[probe["type"]]
                    and http.get("port") == 8080 and http.get("scheme", "HTTP") == "HTTP"
                    and probe.get("tcpSocket") is None and probe.get("exec") is None,
                    "Existing Liveness/Readiness probes must use only their HTTP health routes on port 8080.")
    env = environment(app)
    require(all(value == "Production" for key, value in env.items()
                if key.upper() in ("ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT")),
            "Development and Testing authentication modes cannot be released.")
    forbidden = ("secret", "certificate", "apikey", "connectionstring", "accountkey")
    require(not any(any(part in key.lower().replace("_", "") for part in forbidden) for key in env),
            "Credential-bearing environment keys are outside the managed-identity-only release contract.")
    if kind == "web":
        prefix = "AzureAd__ClientCredentials__"
        allowed = {prefix + "0__SourceType", prefix + "0__ManagedIdentityClientId", prefix + "0__TokenExchangeUrl"}
        require(env.get(prefix + "0__SourceType") == "SignedAssertionFromManagedIdentity"
                and env.get(prefix + "0__TokenExchangeUrl", "api://AzureADTokenExchange/.default") == "api://AzureADTokenExchange/.default"
                and all(key in allowed for key in env if key.replace(":", "__").lower().startswith(prefix.lower())),
                "Web authentication must retain its sole signed managed-identity assertion credential.")
    return current


def validate_pair(web, mcp):
    web_env, mcp_env = environment(web), environment(mcp)
    required_web = ("AzureAd__ClientId", "AzureAd__TenantId", "Mcp__TokenScope", "Mcp__CallerClientId")
    required_mcp = ("Mcp__Audience", "Mcp__TenantId", "Mcp__CallerClientId")
    require(all(isinstance(web_env.get(key), str) and web_env[key] for key in required_web)
            and all(isinstance(mcp_env.get(key), str) and mcp_env[key] for key in required_mcp),
            "Auth settings must be directly inspectable without a secrets-list permission.")
    require(all(GUID.fullmatch(value) and int(value.replace("-", ""), 16) != 0 for value in (
                web_env["AzureAd__ClientId"], web_env["AzureAd__TenantId"], web_env["Mcp__CallerClientId"],
                mcp_env["Mcp__Audience"], mcp_env["Mcp__TenantId"], mcp_env["Mcp__CallerClientId"])),
            "App audiences, tenant and caller settings must be nonempty directory identifiers.")
    require(web_env["AzureAd__ClientId"].lower() != mcp_env["Mcp__Audience"].lower()
            and web_env["AzureAd__TenantId"].lower() == mcp_env["Mcp__TenantId"].lower()
            and web_env["Mcp__CallerClientId"].lower() == mcp_env["Mcp__CallerClientId"].lower()
            and web_env["Mcp__TokenScope"] == f"api://{mcp_env['Mcp__Audience']}/.default"
            and mcp_env["Mcp__Audience"] != "api://AzureADTokenExchange",
            "Presenter OIDC and MCP audiences/caller policy must remain separate and consistent.")


def non_image_settings(app):
    settings = deepcopy(app)
    for key in ("systemData", "etag"):
        settings.pop(key, None)
    for key in READ_ONLY_PROPERTIES:
        settings["properties"].pop(key, None)
    container(settings).pop("image", None)
    return settings


def image_patch(app, image):
    replacement = deepcopy(container(app))
    replacement["image"] = image
    # Merge PATCH preserves omitted template/configuration fields; arrays must be copied whole.
    return {"location": app["location"], "properties": {"template": {"containers": [replacement]}}}


def wait_ready(app_id, before, desired, kind, server, *, seconds=600):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        current = rest(app_id, timeout=min(60, max(1, deadline - time.monotonic())))
        validate_app(current, app_id, kind, server, require_ready=False)
        require(non_image_settings(current) == non_image_settings(before),
                "Non-image settings changed during release; stopped without rollback.")
        properties = current["properties"]
        require(properties.get("provisioningState") in ("InProgress", "Succeeded"),
                "The app entered a failed or unexpected provisioning state.")
        require(container(current)["image"] in (container(before)["image"], desired),
                "A concurrent image change was detected; stopped without rollback.")
        name = properties.get("latestRevisionName")
        if (properties["provisioningState"] == "Succeeded" and container(current)["image"] == desired
                and name and properties.get("latestReadyRevisionName") == name):
            revision = rest(app_id, revision=name, timeout=min(60, max(1, deadline - time.monotonic())))
            require(revision.get("id", "").lower() == f"{app_id}/revisions/{name}".lower(),
                    "The returned revision did not match the requested app revision.")
            state = revision.get("properties", {})
            require(state.get("provisioningState") in ("Provisioning", "Provisioned")
                    and state.get("runningState") not in ("Failed", "Degraded", "Stopped")
                    and state.get("healthState") != "Unhealthy",
                    "The selected revision failed or stopped; no activation or rollback was attempted.")
            require(state.get("template") == current["properties"]["template"],
                    "The selected revision template does not match the app's desired configuration.")
            if (state.get("active") is True and state.get("provisioningState") == "Provisioned"
                    and state.get("healthState") == "Healthy" and state.get("runningState") == "Running"):
                return
        delay = min(5, deadline - time.monotonic())
        if delay > 0:
            time.sleep(delay)
    require(False, "Bounded revision readiness wait expired; no activation, rollback or resource cleanup was attempted.")


def release():
    source = authorize()
    server = registry()
    digests = selected_digests()
    app_ids = {kind: resource_id(os.environ.get(f"{kind.upper()}_CONTAINER_APP_ID", ""), os.environ["AZURE_SUBSCRIPTION_ID"])
               for kind in ("mcp", "web")}
    require(app_ids["web"].lower() != app_ids["mcp"].lower(), "App resource IDs must be distinct.")
    before = {kind: rest(app_id) for kind, app_id in app_ids.items()}
    for kind, app_id in app_ids.items():
        validate_app(before[kind], app_id, kind, server)
    validate_pair(before["web"], before["mcp"])
    for kind in app_ids:
        record("selected", source, kind, digests[kind])
    for kind, app_id in app_ids.items():
        current = rest(app_id)
        validate_app(current, app_id, kind, server)
        require(non_image_settings(current) == non_image_settings(before[kind])
                and container(current)["image"] == container(before[kind])["image"]
                and current["properties"].get("provisioningState") == "Succeeded",
                "The app changed after preflight; stopped without changing it.")
        desired = f"{server}/lumero-{kind}@{digests[kind]}"
        if container(current)["image"] != desired:
            rest(app_id, method="PATCH", body=image_patch(current, desired))
        wait_ready(app_id, current, desired, kind, server)
        record("verified", source, kind, digests[kind])
    print("App/revision readiness verified. This is not live authentication, model quality or end-to-end acceptance.")


if __name__ == "__main__":
    sys.exit(main_entry(release))
