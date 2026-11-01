"""Local-only compiler and focused IaC contract checks; no Azure execution.

Uses an already installed Bicep executable and the Python standard library.
Public pinned modules may be restored by Bicep to its normal cache. Compilation
results stay in memory. Private execution inputs are deliberately never read.
This is a static regression check, not an ARM evaluator or service validation.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys


ROOT = Path(__file__).resolve().parent
BICEP_FILES = (
    "main.bicep",
    "workloads.bicep",
    "types.bicep",
    "modules/foundation.bicep",
    "modules/storage.bicep",
    "modules/foundry-access.bicep",
)
EXAMPLES = ("main.example.bicepparam", "workloads.example.bicepparam")
PINS = {
    "resources/resource-group": "0.4.4",
    "network/virtual-network": "0.10.2",
    "managed-identity/user-assigned-identity": "0.6.0",
    "container-registry/registry": "0.13.1",
    "network/private-dns-zone": "0.8.1",
    "network/private-endpoint": "0.12.1",
    "app/managed-environment": "0.16.0",
    "storage/storage-account/blob-service": "0.2.0",
    "storage/storage-account/table-service": "0.2.0",
    "app/container-app": "0.23.0",
}
MAIN_PARAMETERS = set("""
applicationResourceGroupName location managedResourceGroupName
containerAppsEnvironmentName virtualNetworkName virtualNetworkAddressPrefix
acaSubnetName acaSubnetAddressPrefix privateEndpointSubnetName
privateEndpointSubnetAddressPrefix tablePrivateEndpointName blobPrivateEndpointName
privateDnsVnetLinkName containerRegistryName storageAccountName
logAnalyticsWorkspaceName environmentDiagnosticSettingName webIdentityName
mcpIdentityName githubBuildIdentityName githubReleaseIdentityName
githubBuildFederatedCredentialName githubReleaseFederatedCredentialName
releaseRoleName releaseRoleDefinitionGuid existingFoundrySubscriptionId
existingFoundryResourceGroupName existingFoundryAccountName tags
""".split())
WORKLOAD_PARAMETERS = set("""
applicationResourceGroupName location containerAppsEnvironmentName
containerRegistryName storageAccountName webAppName mcpAppName webImage mcpImage
webIdentityResourceId webIdentityClientId mcpIdentityResourceId mcpIdentityClientId
githubReleaseIdentityResourceId githubReleasePrincipalId releaseRoleDefinitionGuid
tenantId presenterAppClientId mcpApiClientId presenterObjectIds webCanonicalFqdn
mcpCanonicalFqdn foundryEndpoint tags
""".split())
ACTIONS = [
    "Microsoft.App/containerApps/read",
    "Microsoft.App/containerApps/write",
    "Microsoft.App/containerApps/revisions/read",
]
ROLES = {
    "pull": "7f951dda-4ed3-4680-a7ca-43fe172d538d",
    "push": "8311e382-0749-4cb8-b61a-304f252e45ec",
    "table": "0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3",
    "blob": "ba92f5b4-2d11-453d-a403-e96b0029c9fe",
    "openai": "5e0bd9bd-7b93-4f28-af87-19fc36ad61bd",
}
checks = 0


def check(condition: bool, label: str) -> None:
    global checks
    checks += 1
    if not condition:
        # Labels describe contracts; do not dump parameter or expression values.
        raise ValueError(label)


def locate_bicep(explicit: str | None) -> str:
    if explicit:
        path = shutil.which(explicit) or (explicit if Path(explicit).is_file() else None)
        if path:
            return str(Path(path).resolve())
        raise ValueError("The explicitly selected installed Bicep executable was not found.")
    on_path = shutil.which("bicep")
    if on_path:
        return on_path
    config = Path(os.environ.get("AZURE_CONFIG_DIR", str(Path.home() / ".azure")))
    for name in ("bicep.exe", "bicep"):
        cached = config / "bin" / name
        if cached.is_file():
            return str(cached)
    raise ValueError("Bicep is not installed/on PATH or in the existing CLI cache; no installation attempted.")


def command(bicep: str, *args: str) -> str:
    result = subprocess.run(
        [bicep, *args], cwd=ROOT, capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=300, check=False,
    )
    if result.returncode or result.stderr.strip():
        # Only fixed public source/example files are compiled, never private inputs.
        if result.stderr.strip():
            print(result.stderr.strip(), file=sys.stderr)
        raise ValueError(f"Bicep {args[0]} failed or emitted diagnostics; no fallback attempted.")
    return result.stdout


def resources(template: dict) -> dict:
    entries = template.get("resources", {})
    if isinstance(entries, list):
        return {str(index): value for index, value in enumerate(entries)}
    return entries


def module(template: dict, symbol: str) -> tuple[dict, dict]:
    value = resources(template)[symbol]["properties"]
    # Bicep can lift a conditional parameter into an ARM expression returning
    # createObject('value', ...). Keep such wrappers opaque; never evaluate ARM.
    params = {key: item["value"] if isinstance(item, dict) else item
              for key, item in value.get("parameters", {}).items()}
    return value["template"], params


def deployed(template: dict) -> list[dict]:
    # Existing symbolic resources are references, not creates/updates.
    return [item for item in resources(template).values() if not item.get("existing", False)]


def all_resources(template: dict):
    for item in resources(template).values():
        yield item
        properties = item.get("properties", {})
        child = properties.get("template") if isinstance(properties, dict) else None
        if isinstance(child, dict):
            yield from all_resources(child)


def field_values(value):
    if isinstance(value, dict):
        for child in value.values():
            yield from field_values(child)
    elif isinstance(value, list):
        for child in value:
            yield from field_values(child)
    else:
        yield value


def subset(actual: dict, expected: dict, label: str) -> None:
    check(all(actual.get(key) == value for key, value in expected.items()), label)


def section(source: str, start: str, end: str) -> str:
    return source.split(start, 1)[1].split(end, 1)[0]


def environment_pairs(source: str, start: str, end: str) -> dict[str, str]:
    text = section(source, start, end)
    pairs = re.findall(r"name:\s*'([^']+)'\s*,?\s*value:\s*('[^']*'|[^}\r\n]+)", text)
    result = {name: value.strip() for name, value in pairs}
    check(len(result) == len(pairs), "No duplicate environment entries in an authored app block")
    return result


def quoted(value: str) -> str:
    return "'" + value + "'"


def includes_code(source: str, *fragments: str) -> bool:
    # Bicep's formatter wraps long function calls. These fixed code-fragment
    # checks intentionally ignore whitespace, unlike configuration-value checks.
    compact = re.sub(r"\s+", "", source)
    return all(re.sub(r"\s+", "", fragment) in compact for fragment in fragments)


def source_checks(sources: dict[str, str]) -> None:
    authored = "\n".join(sources[name] for name in BICEP_FILES)
    pins = dict(re.findall(r"br/public:avm/res/([^':]+):([^']+)", authored))
    check(pins == PINS, "Exact reviewed and pinned AVM module set")
    check(len(re.findall(r"br/public:avm/res/", authored)) == 13, "No additional or unpinned AVM reference")
    uuids = set(re.findall(r"\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b", authored, re.I))
    check(uuids == set(ROLES.values()), "Only the five public built-in role GUID literals")
    check(not re.search(r"\b(?:\d{1,3}\.){3}\d{1,3}/\d+\b", authored), "No literal IPv4 CIDR in authored templates")
    code = "\n".join(line for line in authored.splitlines() if not line.lstrip().startswith("//"))
    check(not re.search(r"\blist(?:Keys|Credentials)\s*\(", code, re.I), "No key/credential retrieval in authored code")
    for forbidden in (
        "ASPNETCORE_FORWARDEDHEADERS_ENABLED", "Storage__ConnectionString",
        "Foundry__ApiKey", "AzureAd__ClientSecret", "KnownNetworks.Clear", "KnownProxies.Clear",
    ):
        check(forbidden not in code, "No authentication or proxy trust fallback: " + forbidden)
    network = sources["modules/foundation.bicep"]
    check("endsWith(acaSubnetAddressPrefix, '/23')" in network, "Explicit /23 ACA subnet guard")
    check("endsWith(privateEndpointSubnetAddressPrefix, '/27')" in network, "Explicit /27 endpoint subnet guard")
    check("length(union(map(identityNames, name => toLower(name)), [])) == 4" in network, "Four distinct identity names")
    workload = sources["workloads.bicep"]
    check(all(name not in workload for name in ("verifiedProxyCidr", "checkedProxyCidr", "KnownNetworks", "KnownProxies")),
          "Fixed-origin deployment introduces no proxy-address trust")
    images = section(workload, "var checkedImages", "var commonEnvironment")
    check(includes_code(images, "length(split(image, '@sha256:')) == 2",
                        "length(last(split(image, '@sha256:'))) == 64",
                        "!contains(last(split(first(split(image, '@sha256:')), '/')), ':')",
                        "empty(filter(range(0, length(last(split(image, '@sha256:')))), index => !contains('0123456789abcdef', substring(last(split(image, '@sha256:')), index, 1))))",
                        "? image : fail("),
          "Digest-only image guard retains 64 lowercase hexadecimal characters and rejects tags")
    check(includes_code(images, "map([webImage, mcpImage], (image, applicationIndex) =>")
          and includes_code(workload, "image: checkedImages[index]"),
          "Web/MCP image inputs and checked container images retain application order")
    check(includes_code(images, "first(split(image, '@sha256:')) == '${registry.properties.loginServer}/${imageRepositories[applicationIndex]}'"),
          "Image guard binds the actual registry and exact per-app repository before the digest delimiter")
    check(".internal.${managedEnvironment.properties.defaultDomain}" in workload, "Internal MCP canonical hostname")
    check("webCanonicalFqdn == '${webAppName}.${managedEnvironment.properties.defaultDomain}'" in workload,
          "Canonical Web host is bound to the existing environment")
    check(includes_code(workload, "!contains(foundryEndpoint, '?')", "!contains(foundryEndpoint, '#')"),
          "Foundry endpoint rejects query/fragment components")
    check("length(assignableScopes) == 1" in workload and "length(rolePermissions) == 1" in workload
          and "empty(permission.?dataActions ?? [])" in workload, "Existing release-role scope/permission guard")
    for name in ("main.bicep", "workloads.bicep"):
        check("environment().name == 'AzureCloud'" in sources[name], "Public-cloud-only entry point: " + name)


def foundation_checks(main: dict) -> None:
    check(len(deployed(main)) == 4, "Subscription entry point: RG module, foundation, role, existing-account grant only")
    check(not any(item["type"] == "Microsoft.App/containerApps" for item in all_resources(main)), "Foundation excludes apps")
    _, rg = module(main, "applicationResourceGroupModule")
    check(set(rg) == {"name", "location", "tags", "enableTelemetry"} and rg["enableTelemetry"] is False,
          "One application RG without extra grants or telemetry")
    role = resources(main)["releaseRoleDefinition"]
    subset(role, {"apiVersion": "2022-04-01", "name": "[parameters('releaseRoleDefinitionGuid')]"}, "Stable private custom-role GUID/API")
    props = role["properties"]
    check(props["type"] == "CustomRole" and len(props["assignableScopes"]) == 1
          and "applicationResourceGroupModule" in props["assignableScopes"][0], "Only the new RG is assignable")
    check(props["permissions"] == [{"actions": ACTIONS, "notActions": [], "dataActions": [], "notDataActions": []}],
          "Exact three-action custom role with no data actions")
    foundry, params = module(main, "existingFoundryAccessModule")
    grants = deployed(foundry)
    check(len(grants) == 1 and grants[0]["type"] == "Microsoft.Authorization/roleAssignments", "Existing Foundry account has one extension write only")
    check("Microsoft.CognitiveServices/accounts" in grants[0]["scope"] and "existingFoundryAccountName" in grants[0]["scope"], "Foundry account-only assignment scope")
    check(ROLES["openai"] in json.dumps(foundry["variables"]) and "webIdentity" in params["webPrincipalId"], "Web-only OpenAI User grant")
    foundation, _ = module(main, "foundation")
    check(len(deployed(foundation)) == 11, "Fixed foundation module/resource declarations")
    identity_symbols = ("webIdentityModule", "mcpIdentityModule", "githubBuildIdentityModule", "githubReleaseIdentityModule")
    federation_count = 0
    for index, symbol in enumerate(identity_symbols):
        _, identity = module(foundation, symbol)
        check(identity["enableTelemetry"] is False and "roleAssignments" not in identity, "No identity-scope grant or identity telemetry")
        federations = identity.get("federatedIdentityCredentials", [])
        check(len(federations) == (1 if index >= 2 else 0), "Only build/release have ARM federations")
        for trust in federations:
            environment = "lumero-build" if index == 2 else "lumero-release"
            subset(trust, {
                "issuer": "https://token.actions.githubusercontent.com",
                "audiences": ["api://AzureADTokenExchange"],
                "subject": "repo:jarrodkrige@10612771/lumero-lesson-plan-demo@1441161999:environment:" + environment,
            }, "Exact protected GitHub environment federation")
            federation_count += 1
    check(federation_count == 2, "Exactly two ARM federations; Graph trust stays external")
    _, vnet = module(foundation, "virtualNetwork")
    check(len(vnet["subnets"]) == 2 and vnet["dnsServers"] == [], "One VNet, two subnets, Azure DNS")
    check(vnet["subnets"][0]["delegation"] == "Microsoft.App/environments", "Exclusive ACA subnet delegation")
    check(vnet["subnets"][1]["privateEndpointNetworkPolicies"] == "Disabled", "Separate endpoint subnet")
    check(not any(key in json.dumps(vnet) for key in ("natGateway", "networkSecurityGroup", "routeTable", "peerings", "serviceEndpoints")), "No extra customer network topology")
    endpoints = foundation["variables"]["storageEndpoints"]
    check([item["service"] for item in endpoints] == ["table", "blob"], "Exactly Table and Blob endpoint/zone instances")
    _, dns = module(foundation, "privateDnsZones")
    check(len(dns["virtualNetworkLinks"]) == 1 and dns["virtualNetworkLinks"][0]["registrationEnabled"] is False, "One registration-disabled link per private zone")
    _, pe = module(foundation, "privateEndpoints")
    check(len(pe["privateLinkServiceConnections"]) == 1 and len(pe["privateDnsZoneGroup"]["privateDnsZoneGroupConfigs"]) == 1,
          "Each private endpoint uses one approved service/zone")
    _, registry = module(foundation, "containerRegistryModule")
    subset(registry, {
        "acrSku": "Basic", "roleAssignmentMode": "LegacyRegistryPermissions",
        "acrAdminUserEnabled": False, "anonymousPullEnabled": False,
        "azureADAuthenticationAsArmPolicyStatus": "enabled", "publicNetworkAccess": "Enabled",
        "networkRuleSetDefaultAction": "Allow", "networkRuleBypassOptions": "None",
        "networkRuleBypassAllowedForTasks": False, "dataEndpointEnabled": False,
    }, "Basic classic-RBAC registry without admin/anonymous pull or Premium firewall")
    registry_grants = registry["roleAssignments"]
    check([grant["roleDefinitionIdOrName"] for grant in registry_grants] == [ROLES["pull"], ROLES["pull"], ROLES["push"]], "Exactly two AcrPull and one AcrPush grants")
    check(all(symbol in grant["principalId"] for symbol, grant in zip(identity_symbols[:3], registry_grants)), "Registry grants use the intended identity principals")
    storage, _ = module(foundation, "privateStorage")
    check(len(deployed(storage)) == 3, "Storage account and only Blob/Table child modules")
    account = resources(storage)["storageAccount"]
    subset(account, {"apiVersion": "2025-06-01", "kind": "StorageV2", "sku": {"name": "Standard_LRS"}}, "Fixed storage account kind/SKU/API")
    subset(account["properties"], {
        "accessTier": "Hot", "isHnsEnabled": False, "allowSharedKeyAccess": False,
        "allowBlobPublicAccess": False, "publicNetworkAccess": "Disabled", "supportsHttpsTrafficOnly": True,
        "minimumTlsVersion": "TLS1_2", "defaultToOAuthAuthentication": True,
        "networkAcls": {"defaultAction": "Deny", "bypass": "None", "ipRules": [], "virtualNetworkRules": [], "resourceAccessRules": []},
        "encryption": {"keySource": "Microsoft.Storage", "services": {
            "blob": {"enabled": True, "keyType": "Account"}, "table": {"enabled": True, "keyType": "Account"}}},
    }, "Keyless private storage and account-scoped Microsoft-managed Table encryption at creation")
    _, table = module(storage, "tableService")
    check(len(table["tables"]) == 1 and table["tables"][0]["name"] == "lessonplanner", "One fixed Table")
    table_grants = table["tables"][0]["copy"][0]
    check(table_grants["name"] == "roleAssignments"
          and table_grants["count"] == "[length(createArray(parameters('webPrincipalId'), parameters('mcpPrincipalId')))]"
          and table_grants["input"]["roleDefinitionIdOrName"] == ROLES["table"], "Two Table-child grants only")
    _, blob = module(storage, "blobService")
    check(len(blob["containers"]) == 1, "One Blob container; no key blob payload")
    container = blob["containers"][0]
    check(container["name"] == "dataprotection" and container["publicAccess"] == "None", "Private key container")
    check(container["roleAssignments"] == [{"roleDefinitionIdOrName": ROLES["blob"], "principalId": "[parameters('webPrincipalId')]", "principalType": "ServicePrincipal"}], "Web-only container-child Blob contributor")
    check(all(blob[key] is False for key in (
        "automaticSnapshotPolicyEnabled", "changeFeedEnabled", "containerDeleteRetentionPolicyEnabled",
        "deleteRetentionPolicyEnabled", "isVersioningEnabled", "lastAccessTimeTrackingPolicyEnabled", "restorePolicyEnabled",
    )), "No retention/versioning/backup feature added")
    workspace = resources(foundation)["logAnalyticsWorkspaceResource"]
    check(workspace["apiVersion"] == "2025-07-01", "Reviewed workspace API")
    subset(workspace["properties"], {"sku": {"name": "PerGB2018"}, "retentionInDays": 30, "workspaceCapping": {"dailyQuotaGb": "[json('0.1')]"}}, "Fixed logs tier, retention and proposed fractional cap")
    check(workspace["properties"]["features"]["disableLocalAuth"] is True, "Workspace local authentication disabled")
    _, environment = module(foundation, "containerAppsEnvironmentModule")
    subset(environment, {"internal": False, "zoneRedundant": False, "publicNetworkAccess": "Enabled",
                         "workloadProfiles": [{"name": "Consumption", "workloadProfileType": "Consumption"}],
                         "appLogsConfiguration": {"destination": "azure-monitor"}}, "External Consumption environment with keyless Azure Monitor logging")
    check("ingressConfiguration" not in environment and "logAnalyticsWorkspaceResourceId" not in json.dumps(environment), "No Premium ingress or key-based environment logging branch")
    diagnostics = environment["diagnosticSettings"]
    check(len(diagnostics) == 1 and diagnostics[0]["metricCategories"] == [] and diagnostics[0]["logCategoriesAndGroups"] == [
        {"category": "ContainerAppConsoleLogs", "enabled": True}, {"category": "ContainerAppSystemLogs", "enabled": True}], "Only the two approved diagnostic log categories")
    # Count selected assignment inputs, not inactive resources embedded in AVM templates.
    check(len(registry_grants) + 2 + len(container["roleAssignments"]) + len(grants) == 7, "Exactly seven foundation assignment instances")
    for template in (foundation, storage):
        for symbol, item in resources(template).items():
            if item["type"] == "Microsoft.Resources/deployments" and symbol != "privateStorage":
                _, params = module(template, symbol)
                check(params.get("enableTelemetry") is False, "Selected AVM telemetry explicitly disabled")


def image_reference_cases(repositories: list[str]) -> int:
    # Bounded contract model, not an ARM evaluator. Separate source and compiled
    # checks bind this model to the authored guard, repository order and app use.
    registry = "registry.invalid"
    digest = "0123456789abcdef" * 4
    suffix = "@sha256:" + digest
    valid = [f"{registry}/{repository}{suffix}" for repository in repositories]

    def accepted(images: list[str]) -> bool:
        return len(images) == len(repositories) and all(
            re.fullmatch(rf"{re.escape(registry)}/{re.escape(repository)}@sha256:[0-9a-f]{{64}}", image)
            is not None for repository, image in zip(repositories, images)
        )

    check(accepted(valid), "Image-reference model accepts the two correctly bound lowercase-digest controls")
    rejections = 0
    for index, repository in enumerate(repositories):
        prefix = f"{registry}/{repository}"
        invalid = {
            "wrong repository": f"{registry}/unrelated{suffix}",
            "repository case": f"{registry}/{repository.upper()}{suffix}",
            "swapped repository": valid[1 - index],
            "nested namespace": f"{registry}/nested/{repository}{suffix}",
            "nested child": f"{prefix}/nested{suffix}",
            "tag-only": f"{prefix}:latest",
            "tag-plus-digest": f"{prefix}:latest{suffix}",
            "uppercase digest": f"{prefix}@sha256:{digest.upper()}",
            "short digest": f"{prefix}@sha256:{digest[:-1]}",
            "non-hex digest": f"{prefix}@sha256:{'g' * 64}",
            "different registry": f"other.invalid/{repository}{suffix}",
        }
        for label, image in invalid.items():
            images = valid.copy()
            images[index] = image
            check(not accepted(images), f"Image-reference model rejects {label} for {repository}")
            rejections += 1
    check(not accepted(valid[::-1]), "Image-reference model rejects both repositories being swapped")
    return rejections + 1


def workload_checks(workloads: dict, source: str) -> None:
    check(len(deployed(workloads)) == 1, "Workloads deploy only the Container App module collection")
    check(workloads["variables"]["releaseActions"] == ACTIONS, "Release-role verification uses exact approved actions")
    applications = workloads["variables"]["applications"]
    check(len(applications) == 2, "Exactly two apps")
    check(workloads["variables"]["imageRepositories"] == ["lumero-web", "lumero-mcp"],
          "Exact Web/MCP repository binding in application order")
    subset(applications[0], {"role": "web", "external": True, "cpu": 1, "memory": "2Gi", "identityResourceId": "[parameters('webIdentityResourceId')]"}, "Fixed external Web sizing and identity")
    subset(applications[1], {"role": "mcp", "external": False, "cpu": "[json('0.25')]", "memory": "0.5Gi", "identityResourceId": "[parameters('mcpIdentityResourceId')]"}, "Fixed internal MCP sizing and identity")
    _, app = module(workloads, "containerApps")
    subset(app, {"enableTelemetry": False, "workloadProfileName": "Consumption", "activeRevisionsMode": "Single",
                 "disableIngress": False, "ingressAllowInsecure": False, "ingressTransport": "auto", "ingressTargetPort": 8080,
                 "traffic": [{"latestRevision": True, "weight": 100}], "scaleSettings": {"minReplicas": 1, "maxReplicas": 1, "rules": []}}, "Fixed HTTPS ingress, revision and replica controls")
    check(app["managedIdentities"]["systemAssigned"] is False and len(app["managedIdentities"]["userAssignedResourceIds"]) == 1, "One UAMI per app; no system identity")
    check(len(app["registries"]) == 1 and set(app["registries"][0]) == {"server", "identity"}, "Identity-only image pull without registry credentials")
    check(len(app["containers"]) == 1 and len(app["roleAssignments"]) == 1, "One container and one release assignment per app")
    grant = app["roleAssignments"][0]
    check(grant["principalType"] == "ServicePrincipal" and "releaseIdentityExisting" in grant["principalId"]
          and "releaseRoleExisting" in grant["roleDefinitionIdOrName"], "Checked release principal and custom role, scoped by the app module")
    probes = app["containers"][0]["probes"]
    check(len(probes) == 3, "Exactly startup, liveness and readiness probes")
    check(probes[0] == {"type": "Startup", "tcpSocket": {"port": 8080}, "initialDelaySeconds": 10, "periodSeconds": 5, "timeoutSeconds": 3, "failureThreshold": 24, "successThreshold": 1}, "Exact startup TCP probe")
    for probe, kind, path, timeout in ((probes[1], "Liveness", "/health/live", 3), (probes[2], "Readiness", "/health/ready", 6)):
        subset(probe, {"type": kind, "periodSeconds": 10, "timeoutSeconds": timeout, "failureThreshold": 3, "successThreshold": 1}, "Exact " + kind + " timing")
        http = probe["httpGet"]
        subset(http, {"path": path, "port": 8080, "scheme": "HTTP"}, "Actual " + kind + " HTTP route")
        check(len(http["httpHeaders"]) == 1 and http["httpHeaders"][0]["name"] == "Host"
              and "webCanonicalFqdn" in http["httpHeaders"][0]["value"] and "mcpCanonicalFqdn" in http["httpHeaders"][0]["value"], "Per-app canonical probe Host")
    common = environment_pairs(source, "var commonEnvironment", "var presenterEnvironment")
    expected_common = {name: quoted(value) for name, value in {
        "ASPNETCORE_ENVIRONMENT": "Production", "ASPNETCORE_HTTP_PORTS": "8080",
        "Logging__LogLevel__Default": "Warning", "Logging__LogLevel__Microsoft.AspNetCore": "Warning",
        "Logging__LogLevel__Azure": "Warning", "Logging__LogLevel__ModelContextProtocol": "Warning",
        "Storage__TableName": "lessonplanner", "Quiz__PublicBaseUrl": "https://${checkedWebFqdn}",
        "Quiz__LifetimeHours": "24", "Quiz__ApprovalMinutes": "5", "Quiz__MaximumAttempts": "200",
    }.items()}
    expected_common["Storage__TableServiceUri"] = "storageAccount.properties.primaryEndpoints.table"
    check(common == expected_common, "Exact common production/storage/quiz/logging environment contract")
    web = environment_pairs(source, "var webEnvironment", "var mcpEnvironment")
    expected_web = {name: quoted(value) for name, value in {
        "Logging__LogLevel__Microsoft.Identity.Web": "Warning", "Logging__LogLevel__System.Net.Http.HttpClient": "Warning",
        "Logging__LogLevel__Microsoft.Agents.AI": "Warning", "AzureAd__CallbackPath": "/signin-oidc",
        "AzureAd__ClientCredentials__0__SourceType": "SignedAssertionFromManagedIdentity",
        "AzureAd__ClientCredentials__0__TokenExchangeUrl": "api://AzureADTokenExchange/.default",
        "Mcp__Endpoint": "https://${checkedMcpFqdn}/mcp", "Mcp__TokenScope": "api://${checkedMcpApiClientId}/.default",
        "DataProtection__BlobUri": "${storageAccount.properties.primaryEndpoints.blob}dataprotection/lumero-keys.xml",
        "Foundry__Deployment": "gpt-4.1", "ReverseProxy__UseFixedHttpsOrigin": "true",
    }.items()}
    expected_web.update({"AllowedHosts": "checkedWebFqdn", "AzureAd__Instance": "environment().authentication.loginEndpoint",
                         "AzureAd__TenantId": "tenantId", "AzureAd__ClientId": "presenterAppClientId",
                         "Foundry__Endpoint": "checkedFoundryEndpoint"})
    for name in ("AzureAd__ClientCredentials__0__ManagedIdentityClientId", "Mcp__CallerClientId", "Mcp__ManagedIdentityClientId",
                 "Storage__ManagedIdentityClientId", "DataProtection__ManagedIdentityClientId", "Foundry__ManagedIdentityClientId"):
        expected_web[name] = "checkedWebClientId"
    check(web == expected_web, "Exact Web OIDC/assertion, fixed HTTPS origin, DataProtection, Foundry and MCP caller contract")
    mcp = environment_pairs(source, "var mcpEnvironment", "var checkedWebAppName")
    check(mcp == {"AllowedHosts": "checkedMcpFqdn", "Mcp__TenantId": "tenantId", "Mcp__Audience": "checkedMcpApiClientId",
                  "Mcp__CallerClientId": "checkedWebClientId", "Mcp__RequiredRole": "'Quiz.Publish'", "Storage__ManagedIdentityClientId": "checkedMcpClientId"}, "Separate MCP audience and Table identity; no Web-only settings")
    check(includes_code(source, "Presenter__AllowedObjectIds__${index}", "], presenterEnvironment)"), "Explicit indexed presenter allowlist")
    check(len(applications) * len(app["roleAssignments"]) == 2, "Exactly two workload release grants; nine Azure assignments overall")
    infrastructure_source = (ROOT.parent / "src/LessonPlanner.Infrastructure/InfrastructureRegistration.cs").read_text(encoding="utf-8")
    check("timeout: TimeSpan.FromSeconds(5)" in infrastructure_source, "Application Table readiness timeout remains five seconds")
    routes = (ROOT.parent / "src/LessonPlanner.ServiceDefaults/Extensions.cs").read_text(encoding="utf-8")
    check('"/health/live"' in routes and '"/health/ready"' in routes, "Probe routes exist in application source")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bicep", help="Path to an already installed Bicep executable; never auto-installed")
    args = parser.parse_args()
    bicep = locate_bicep(args.bicep)
    version = command(bicep, "--version").strip()
    check(version.startswith("Bicep CLI version "), "Selected executable must be native Bicep")
    print(version)
    sources = {name: (ROOT / name).read_text(encoding="utf-8") for name in (*BICEP_FILES, *EXAMPLES)}
    for name in sources:
        command(bicep, "lint", name)
        formatted = command(bicep, "format", name, "--stdout", "--newline-kind", "LF", "--indent-kind", "Space", "--indent-size", "2", "--insert-final-newline")
        check(formatted.replace("\r\n", "\n") == sources[name], "Formatting: " + name)
    print("PASS: lint and formatting, 6 Bicep files and 2 parameter examples; no diagnostics.")
    templates = {entry: json.loads(command(bicep, "build", entry + ".bicep", "--stdout", "--no-restore")) for entry in ("main", "workloads")}
    for entry, parameters in (("main", MAIN_PARAMETERS), ("workloads", WORKLOAD_PARAMETERS)):
        template = templates[entry]
        check(set(template["parameters"]) == parameters, "Complete required parameter contract: " + entry)
        check(all("defaultValue" not in item for item in template["parameters"].values()), "No deployment defaults: " + entry)
        built = json.loads(command(bicep, "build-params", entry + ".example.bicepparam", "--stdout", "--no-restore"))
        parameter_document = json.loads(built["parametersJson"])
        check(set(parameter_document["parameters"]) == parameters, "Complete compile-only parameter example: " + entry)
        check(all(isinstance(value, str) and (("<" in value and ">" in value) or ".invalid" in value)
                  for value in field_values(parameter_document["parameters"])), "Every example value is an invalid placeholder: " + entry)
        if entry == "workloads":
            check({name: parameter_document["parameters"][name]["value"] for name in ("webImage", "mcpImage")} == {
                "webImage": "registry.invalid/lumero-web@sha256:<required-immutable-digest>",
                "mcpImage": "registry.invalid/lumero-mcp@sha256:<required-immutable-digest>",
            }, "Invalid image examples preserve the fixed per-app repository contract")
    check(set(templates["main"]["outputs"]) == set("applicationResourceGroup containerAppsEnvironment network storage containerRegistry identities logAnalyticsWorkspace releaseRole existingFoundryAccess".split()), "Nine non-secret foundation output objects")
    check(set(templates["workloads"]["outputs"]) == {"web", "mcp", "releaseRoleAssignmentResourceIds"}, "Three non-secret workload outputs")
    print("PASS: both entry points and both deliberately invalid examples compile in memory.")
    source_checks(sources)
    foundation_checks(templates["main"])
    workload_checks(templates["workloads"], sources["workloads.bicep"])
    rejections = image_reference_cases(templates["workloads"]["variables"]["imageRepositories"])
    print(f"PASS: image-reference model; 2 valid controls and {rejections} bounded rejection cases in memory (not ARM execution).")
    print(f"PASS: {checks} static compiler/contract checks; 4 UAMIs, 2 ARM federations, 7+2 Azure assignments, 2 gated apps.")
    print("NOT PROVED: live HTTPS/Host enforcement, service cap acceptance, Azure deployment/runtime behaviour. No cloud execution performed.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError, subprocess.TimeoutExpired) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)
