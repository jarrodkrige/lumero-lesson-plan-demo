"""Build locally, then push only the approved repositories after explicit review."""

import json
import os
import platform
import sys
from urllib.parse import urlencode
from urllib.request import Request

from common import DIGEST, REPOSITORY, ROOT, SHA, authorize, command, main_entry, private_request, record, registry, require
from smoke import startup

APPS = {"web": "LessonPlanner.Web", "mcp": "LessonPlanner.Mcp"}


def check_context():
    rules = [line.strip() for line in (ROOT / ".dockerignore").read_text(encoding="utf-8").splitlines()
             if line.strip() and not line.lstrip().startswith("#")]
    projects = ("Core", "Agents", "Infrastructure", "ServiceDefaults", "Web", "Mcp")
    includes = {"!global.json", "!Directory.Build.props", "!Directory.Packages.props", "!src/"}
    for project in projects:
        includes.update((f"!src/LessonPlanner.{project}/", f"!src/LessonPlanner.{project}/**"))
    require(rules and rules[0] == "**" and set(rule for rule in rules if rule.startswith("!")) == includes,
            "Docker context must remain a deny-by-default production-source allowlist.")
    require(rules.count("!src/") == 1 and rules.count("src/*") == 1
            and rules.index("!src/") < rules.index("src/*")
            and all(index > rules.index("src/*") for index, rule in enumerate(rules)
                    if rule.startswith("!src/") and rule != "!src/"),
            "Docker context requires src/* after !src/ and before every production child inclusion.")
    last_include = max(index for index, rule in enumerate(rules) if rule.startswith("!"))
    exclusions = ("**/bin/", "**/obj/", "**/Properties/", "**/.*", "**/*.user", "**/appsettings.*.json",
                  "**/*secrets*", "**/*.key", "**/*.pfx", "**/*.p12", "**/*.pem", "**/*.log", "**/*.trx")
    require(all(rule in rules[last_include + 1:] for rule in exclusions),
            "Docker context exclusions must follow every production-source inclusion.")
    require(not any((ROOT / "src" / project / "Dockerfile.dockerignore").exists() for project in APPS.values()),
            "Dockerfile-specific ignore files must not override the root context boundary.")


def local_image(app, source):
    return f"lumero-{app}:{source}"


def inspect_local(app, source):
    value = json.loads(command(["docker", "image", "inspect", local_image(app, source)]))
    require(isinstance(value, list) and len(value) == 1, "A single built image is required.")
    image = value[0]
    labels = image.get("Config", {}).get("Labels", {}) or {}
    require(image.get("Os") == "linux" and image.get("Architecture") == "amd64"
            and DIGEST.fullmatch(image.get("Id", ""))
            and labels.get("org.opencontainers.image.revision") == source
            and labels.get("org.opencontainers.image.source") == f"https://github.com/{REPOSITORY}",
            "Built image platform or source provenance did not match.")
    config = image.get("Config", {})
    user = config.get("User", "").split(":")[0]
    env = dict(value.split("=", 1) for value in config.get("Env", []) if "=" in value)
    require(user.isdecimal() and int(user) > 0
            and set(config.get("ExposedPorts", {})) == {"8080/tcp"}
            and env.get("ASPNETCORE_HTTP_PORTS") == "8080"
            and env.get("ASPNETCORE_ENVIRONMENT") == "Production"
            and env.get("DOTNET_ENVIRONMENT", "Production") == "Production"
            and not env.get("ASPNETCORE_URLS") and not env.get("ASPNETCORE_HTTPS_PORTS")
            and config.get("Entrypoint") == ["dotnet", f"{APPS[app]}.dll"]
            and not config.get("Cmd"),
            "Built image must retain its non-root production entrypoint and only port 8080.")
    return image["Id"]


def native_amd64():
    require(platform.system() == "Linux" and platform.machine().lower() in ("amd64", "x86_64"),
            "Build and runtime validation require native Linux AMD64; .NET under QEMU is not supported.")
    daemon = json.loads(command(["docker", "info", "--format", '{{json .}}']))
    require(daemon.get("OSType") == "linux" and daemon.get("Architecture") in ("amd64", "x86_64"),
            "The Docker daemon must also be native Linux AMD64.")


def build_images(source):
    require(SHA.fullmatch(source), "An exact source SHA is required for local image validation.")
    native_amd64()
    check_context()
    for app, project in APPS.items():
        print(f"Building lumero-{app} locally; build diagnostics are withheld.", flush=True)
        command([
            "docker", "buildx", "build", "--builder", "default", "--platform", "linux/amd64", "--load",
            "--provenance=false", "--sbom=false", "--pull",
            "--file", f"src/{project}/Dockerfile", "--tag", local_image(app, source),
            "--label", f"org.opencontainers.image.revision={source}",
            "--label", f"org.opencontainers.image.source=https://github.com/{REPOSITORY}", ".",
        ], timeout=900)
        inspect_local(app, source)
        startup(app, local_image(app, source))
    print("Both production images passed network-isolated startup; nothing was pushed and no cloud acceptance is implied.")


def build():
    build_images(authorize())


def validate():
    build_images(command(["git", "rev-parse", "HEAD"]).strip())


def registry_login(server):
    access_token = command([
        "az", "account", "get-access-token", "--resource", "https://containerregistry.azure.net",
        "--query", "accessToken", "--output", "tsv", "--only-show-errors",
    ]).strip()
    require(access_token and "\n" not in access_token, "Registry identity token acquisition failed.")
    data = urlencode({
        "grant_type": "access_token", "service": server,
        "tenant": os.environ["AZURE_TENANT_ID"], "access_token": access_token,
    }).encode("utf-8")
    request = Request(f"https://{server}/oauth2/exchange", data=data,
                      headers={"Content-Type": "application/x-www-form-urlencoded"}, method="POST")
    refresh_token = private_request(request)["refresh_token"]
    require(isinstance(refresh_token, str) and refresh_token and "\n" not in refresh_token,
            "Registry identity response was invalid.")
    # The null GUID is ACR's documented token-login username, not an account identifier.
    command(["docker", "login", server, "--username", "00000000-0000-0000-0000-000000000000",
             "--password-stdin"], timeout=60, stdin=refresh_token + "\n")


def pushed_digest(reference, expected_config):
    images = json.loads(command(["docker", "image", "inspect", reference]))
    require(isinstance(images, list) and len(images) == 1, "Pushed image metadata was not singular.")
    repository = reference.rsplit(":", 1)[0]
    digests = {value.removeprefix(repository + "@") for value in images[0].get("RepoDigests", [])
               if value.startswith(repository + "@")}
    require(len(digests) == 1 and all(DIGEST.fullmatch(value) for value in digests),
            "The registry push did not yield exactly one immutable repository digest.")
    digest = digests.pop()
    manifest = json.loads(command(["docker", "manifest", "inspect", f"{repository}@{digest}"], timeout=60))
    require(manifest.get("schemaVersion") == 2 and "manifests" not in manifest
            and manifest.get("mediaType") in (
                "application/vnd.docker.distribution.manifest.v2+json", "application/vnd.oci.image.manifest.v1+json")
            and manifest.get("config", {}).get("digest") == expected_config,
            "The immutable registry manifest did not match the validated local image.")
    return digest


def push():
    source = authorize()
    server = registry()
    configs = {app: inspect_local(app, source) for app in APPS}
    registry_login(server)
    tag = f"sha-{source}-run-{os.environ['GITHUB_RUN_ID']}-attempt-{os.environ['GITHUB_RUN_ATTEMPT']}"
    for app in APPS:
        reference = f"{server}/lumero-{app}:{tag}"
        command(["docker", "image", "tag", local_image(app, source), reference])
        command(["docker", "push", reference], timeout=600)
        digest = pushed_digest(reference, configs[app])
        record("pushed", source, app, digest)
    print("Only digest/source evidence was recorded; registry details and tool output were not published.")


if __name__ == "__main__":
    def main():
        require(len(sys.argv) == 2 and sys.argv[1] in ("validate", "build", "push"), "Choose validate, build or push.")
        {"validate": validate, "build": build, "push": push}[sys.argv[1]]()
    sys.exit(main_entry(main))
