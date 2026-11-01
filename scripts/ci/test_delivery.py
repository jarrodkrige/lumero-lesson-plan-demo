"""Pure mocked delivery checks: no cloud, Docker, services, credentials or filesystem writes."""

import base64
from contextlib import redirect_stderr, redirect_stdout
from copy import deepcopy
from io import StringIO
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import Mock, call, mock_open, patch
from urllib.parse import parse_qs, urlsplit
from urllib.request import Request
from uuid import UUID
import xml.etree.ElementTree as ET

import auth
import common
import images
import release
import smoke
import verify_trx

SOURCE = "a" * 40
OLD_DIGEST = "sha256:" + "0" * 64
DIGESTS = {"web": "sha256:" + "1" * 64, "mcp": "sha256:" + "2" * 64}
SERVER = "cifixture.azurecr.io"
SUBSCRIPTION = str(UUID(int=101))


def context():
    return {
        "SOURCE_SHA": SOURCE, "GITHUB_ACTIONS": "true", "GITHUB_REPOSITORY": common.REPOSITORY,
        "GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_REF": common.REF, "GITHUB_SHA": SOURCE,
        "GITHUB_WORKFLOW_SHA": SOURCE,
        "GITHUB_WORKFLOW_REF": f"{common.REPOSITORY}/.github/workflows/delivery.yml@{common.REF}",
        "RUNNER_ENVIRONMENT": "github-hosted", "RELEASE_CONFIRMATION": "release",
        "GITHUB_RUN_ID": "1", "GITHUB_RUN_ATTEMPT": "1",
    }


def app_id(kind):
    return f"/subscriptions/{SUBSCRIPTION}/resourceGroups/ci-fixture/providers/Microsoft.App/containerApps/{kind}-fixture"


def settings():
    return context() | {
        "AZURE_CLIENT_ID": str(UUID(int=102)), "AZURE_TENANT_ID": str(UUID(int=103)),
        "AZURE_SUBSCRIPTION_ID": SUBSCRIPTION, "ACR_LOGIN_SERVER": SERVER,
        "WEB_CONTAINER_APP_ID": app_id("web"), "MCP_CONTAINER_APP_ID": app_id("mcp"),
        "WEB_DIGEST": DIGESTS["web"], "MCP_DIGEST": DIGESTS["mcp"],
        "AZURE_CONFIG_DIR": str(common.STATE / "azure"), "DOCKER_CONFIG": str(common.STATE / "docker"),
        "ACTIONS_ID_TOKEN_REQUEST_URL": "https://oidc.actions.githubusercontent.com/token?api-version=2.0",
        "ACTIONS_ID_TOKEN_REQUEST_TOKEN": "synthetic-runner-token",
    }


def app_fixture(kind):
    identity = f"/subscriptions/{SUBSCRIPTION}/resourceGroups/ci-fixture/providers/Microsoft.ManagedIdentity/userAssignedIdentities/{kind}-fixture"
    env = smoke.configuration(kind)
    return {
        "id": app_id(kind), "name": f"{kind}-fixture", "type": "Microsoft.App/containerApps", "location": "fixture-region",
        "tags": {"fixture": "true"}, "etag": "fixture-etag", "systemData": {"createdAt": "fixture"},
        "identity": {"type": "UserAssigned", "userAssignedIdentities": {identity: {}}},
        "properties": {
            "environmentId": "synthetic-environment-reference", "workloadProfileName": "Consumption",
            "provisioningState": "Succeeded", "latestRevisionName": f"{kind}-fixture--old",
            "latestReadyRevisionName": f"{kind}-fixture--old", "runningStatus": "Running",
            "configuration": {
                "activeRevisionsMode": "Single", "dapr": None, "secrets": [],
                "registries": [{"server": SERVER, "identity": identity}],
                "ingress": {"external": kind == "web", "targetPort": 8080, "allowInsecure": False,
                            "traffic": [{"latestRevision": True, "weight": 100}]},
            },
            "template": {
                "scale": {"minReplicas": 1, "maxReplicas": 1, "rules": []}, "volumes": [],
                "containers": [{
                    "name": kind, "image": f"{SERVER}/lumero-{kind}@{OLD_DIGEST}",
                    "resources": {"cpu": 1, "memory": "2Gi"} if kind == "web" else {"cpu": 0.25, "memory": "0.5Gi"},
                    "env": [{"name": key, "value": value} for key, value in env.items()],
                    "probes": [
                        {"type": "Startup", "tcpSocket": {"port": 8080}, "initialDelaySeconds": 10,
                         "periodSeconds": 5, "timeoutSeconds": 3, "failureThreshold": 24, "successThreshold": 1},
                        *[{"type": probe, "httpGet": {"path": route, "port": 8080, "scheme": "HTTP",
                                                     "httpHeaders": [{"name": "Host", "value": f"{kind}-fixture.invalid"}]},
                           "periodSeconds": 10, "timeoutSeconds": timeout, "failureThreshold": 3, "successThreshold": 1}
                          for probe, route, timeout in (("Liveness", "/health/live", 3), ("Readiness", "/health/ready", 6))],
                    ],
                }],
            },
        },
    }


def change(value, keys, replacement):
    for key in keys[:-1]:
        value = value[key]
    value[keys[-1]] = replacement


def set_env(app, key, value):
    entry = next(entry for entry in release.container(app)["env"] if entry["name"] == key)
    entry["value"] = value


def image_fixture(kind="web"):
    return {
        "Os": "linux", "Architecture": "amd64", "Id": OLD_DIGEST,
        "Config": {"User": "1654", "ExposedPorts": {"8080/tcp": {}}, "Entrypoint": ["dotnet", f"{images.APPS[kind]}.dll"],
                   "Env": ["ASPNETCORE_HTTP_PORTS=8080", "ASPNETCORE_ENVIRONMENT=Production"],
                   "Labels": {"org.opencontainers.image.revision": SOURCE,
                              "org.opencontainers.image.source": f"https://github.com/{common.REPOSITORY}"}},
    }


def claims():
    return {
        "iss": auth.ISSUER, "aud": auth.AUDIENCE,
        "sub": "repo:jarrodkrige@10612771/lumero-lesson-plan-demo@1441161999:environment:lumero-build",
        "repository": common.REPOSITORY, "ref": common.REF, "sha": SOURCE,
        "workflow_ref": context()["GITHUB_WORKFLOW_REF"], "workflow_sha": SOURCE,
        "event_name": "workflow_dispatch", "runner_environment": "github-hosted",
    }


def revision_fixture(app):
    return {"id": app["id"] + "/revisions/" + app["properties"]["latestRevisionName"],
            "properties": {"active": True, "provisioningState": "Provisioned", "healthState": "Healthy",
                           "runningState": "Running", "template": deepcopy(app["properties"]["template"])}}


class OfflineCase(unittest.TestCase):
    def setUp(self):
        self.enterContext(redirect_stdout(StringIO()))
        self.addCleanup(patch.stopall)
        patch("common.subprocess.run", side_effect=AssertionError("Unexpected subprocess in an offline test")).start()
        patch("urllib.request.OpenerDirector.open", side_effect=AssertionError("Unexpected network in an offline test")).start()


class GuardTests(OfflineCase):
    def test_exact_authorization(self):
        self.assertEqual(common.authorize(context(), SOURCE), SOURCE)

    def test_each_required_binding_is_enforced(self):
        for key in context():
            with self.subTest(key=key):
                value = context()
                value.pop(key)
                with self.assertRaises(common.Refusal):
                    common.authorize(value, SOURCE)
        for updates in ({"RUNNER_DEBUG": "1"}, {"GITHUB_EVENT_NAME": "pull_request"},
                        {"RUNNER_ENVIRONMENT": "self-hosted"}, {"GITHUB_REF": "refs/heads/main"},
                        {"GITHUB_RUN_ID": "0"}, {"SOURCE_SHA": SOURCE.upper()}, {"RELEASE_CONFIRMATION": "Release"}):
            with self.subTest(updates=updates), self.assertRaises(common.Refusal):
                common.authorize(context() | updates, SOURCE)

    def test_checkout_must_equal_reviewed_workflow(self):
        with patch("common.command", return_value="b" * 40) as run, self.assertRaises(common.Refusal):
            common.authorize(context())
        run.assert_called_once_with(["git", "rev-parse", "HEAD"])

    def test_current_build_and_release_settings_are_sufficient(self):
        for kind in ("build", "release"):
            with self.subTest(kind=kind), patch.dict(os.environ, settings(), clear=True), \
                    patch("common.command", return_value=SOURCE) as run, \
                    patch.object(Path, "mkdir"), patch.object(Path, "chmod"):
                common.prepare(kind)
            run.assert_called_once_with(["git", "rev-parse", "HEAD"])

    def test_subprocess_default_and_explicit_timeouts_are_preserved(self):
        for options, seconds in (({}, 60), ({"timeout": 30}, 30), ({"timeout": 900}, 900)):
            with self.subTest(seconds=seconds), patch("common.subprocess.run", return_value=Mock(returncode=0, stdout="done")) as run:
                self.assertEqual(common.command(["synthetic-command"], **options), "done")
            run.assert_called_once()
            self.assertEqual(run.call_args.kwargs["timeout"], seconds)
            self.assertTrue(run.call_args.kwargs["capture_output"])

    def test_registry_and_exact_resource_ids(self):
        self.assertEqual(common.registry(settings()), SERVER)
        self.assertEqual(common.resource_id(app_id("web"), SUBSCRIPTION), app_id("web"))
        for value in ("https://" + SERVER, SERVER + "/repo", "user@" + SERVER, SERVER + ":443", "localhost", SERVER.upper()):
            with self.subTest(value=value), self.assertRaises(common.Refusal):
                common.registry({"ACR_LOGIN_SERVER": value})
        for value in (app_id("web") + "/revisions/other", app_id("web") + "?api-version=other",
                      app_id("web").replace("ci-fixture", ".."), app_id("web").replace("ci-fixture", "."),
                      app_id("web").replace(SUBSCRIPTION, str(UUID(int=999)))):
            with self.subTest(value=value), self.assertRaises(common.Refusal):
                common.resource_id(value, SUBSCRIPTION)

    def test_preparation_only_creates_isolated_tool_state(self):
        with patch.dict(os.environ, settings(), clear=True), patch("common.authorize"), \
                patch.object(Path, "mkdir") as mkdir, patch.object(Path, "chmod") as chmod:
            common.prepare("release")
        self.assertEqual(mkdir.call_count, 3)
        self.assertEqual(chmod.call_args_list, [call(0o700), call(0o700)])

    def test_preparation_fails_before_state_for_bad_identity_or_apps(self):
        for updates in ({"AZURE_CLIENT_ID": ""}, {"AZURE_TENANT_ID": str(UUID(int=0))},
                        {"WEB_CONTAINER_APP_ID": app_id("mcp")}, {"WEB_DIGEST": "latest"}):
            with self.subTest(updates=updates), patch.dict(os.environ, settings() | updates, clear=True), \
                    patch("common.authorize"), patch.object(Path, "mkdir") as mkdir, \
                    self.assertRaises(common.Refusal):
                common.prepare("release")
            mkdir.assert_not_called()

    def test_digests_cannot_be_tags_or_image_references(self):
        self.assertEqual(common.selected_digests(settings()), DIGESTS)
        for value in ("", "latest", "sha256:" + "f" * 63, "sha256:" + "F" * 64, SERVER + "@" + OLD_DIGEST):
            with self.subTest(value=value), self.assertRaises(common.Refusal):
                common.selected_digests(settings() | {"MCP_DIGEST": value})

    def test_subprocess_failures_and_timeouts_never_expose_output(self):
        for failure in (Mock(returncode=1, stdout="synthetic-private-output", stderr="synthetic-provider-token"),
                        subprocess.TimeoutExpired(["private-command"], 1, output="synthetic-private-output")):
            with self.subTest(failure=type(failure).__name__):
                with patch("common.subprocess.run") as run:
                    if isinstance(failure, Exception):
                        run.side_effect = failure
                    else:
                        run.return_value = failure
                    with self.assertRaises(common.Refusal) as caught:
                        common.command(["private-command"], stdin="synthetic-token")
                    self.assertNotIn("synthetic", str(caught.exception))
                    self.assertTrue(run.call_args.kwargs["capture_output"])

    def test_unexpected_provider_errors_are_sanitized(self):
        output = StringIO()
        with redirect_stderr(output):
            result = common.main_entry(Mock(side_effect=ValueError("synthetic-private-response")))
        self.assertEqual(result, 1)
        self.assertNotIn("synthetic-private-response", output.getvalue())

    def test_private_http_is_bounded_and_disallows_proxy_and_redirects(self):
        response = Mock(status=200)
        response.read.return_value = b'{"value":"synthetic"}'
        with patch.dict(os.environ, {}, clear=True), patch("common.build_opener") as build:
            build.return_value.open.return_value.__enter__.return_value = response
            self.assertEqual(common.private_request(Request("https://fixture.invalid")), {"value": "synthetic"})
        self.assertEqual(build.call_args.args[0].proxies, {})
        build.return_value.open.assert_called_once()
        self.assertEqual(build.return_value.open.call_args.kwargs["timeout"], 30)
        response.read.assert_called_once_with(1024 * 1024 + 1)
        with self.assertRaises(common.Refusal):
            common.NoRedirect().redirect_request(None, None, 302, "", {}, "https://other.invalid")

    def test_private_http_refuses_oversized_response(self):
        response = Mock(status=200)
        response.read.return_value = b"x" * (1024 * 1024 + 1)
        with patch("common.build_opener") as build:
            build.return_value.open.return_value.__enter__.return_value = response
            with self.assertRaises(common.Refusal):
                common.private_request(Request("https://fixture.invalid"))

    def test_evidence_only_contains_valid_source_and_digest(self):
        output = StringIO()
        with patch.dict(os.environ, {"GITHUB_OUTPUT": "mock-output", "GITHUB_STEP_SUMMARY": "mock-summary"}, clear=True), \
                patch("builtins.open", mock_open()) as opened, redirect_stdout(output):
            common.record("pushed", SOURCE, "web", DIGESTS["web"])
        self.assertNotIn(SERVER, output.getvalue())
        opened().write.assert_any_call("web_digest=" + DIGESTS["web"] + "\n")
        with self.assertRaises(common.Refusal):
            common.record("validated source", SOURCE)


class AuthTests(OfflineCase):
    def test_immutable_subject_binds_both_ids_and_each_protected_environment(self):
        prefix = "repo:jarrodkrige@10612771/lumero-lesson-plan-demo@1441161999"
        for environment in ("lumero-build", "lumero-release"):
            with self.subTest(environment=environment):
                auth.validate_claims(claims() | {"sub": f"{prefix}:environment:{environment}"}, environment, SOURCE)
                for rejected in (f"repo:{common.REPOSITORY}", prefix.replace("10612771", "10612772"),
                                 prefix.replace("1441161999", "1441162000")):
                    with self.subTest(rejected=rejected), self.assertRaises(common.Refusal):
                        auth.validate_claims(claims() | {"sub": f"{rejected}:environment:{environment}"}, environment, SOURCE)

    def test_oidc_claims_bind_source_environment_and_audience(self):
        auth.validate_claims(claims(), "lumero-build", SOURCE)
        for key in claims():
            with self.subTest(key=key), self.assertRaises(common.Refusal):
                auth.validate_claims(claims() | {key: "unapproved"}, "lumero-build", SOURCE)
        with self.assertRaises(common.Refusal):
            auth.validate_claims(claims(), "lumero-release", SOURCE)

    def test_oidc_request_and_cli_login_are_captured_not_logged(self):
        payload = base64.urlsafe_b64encode(json.dumps(claims()).encode()).decode().rstrip("=")
        token = "synthetic." + payload + ".signature"
        output = StringIO()
        with patch.dict(os.environ, settings(), clear=True), patch("auth.prepare"), \
                patch("auth.authorize", return_value=SOURCE), patch("auth.private_request", return_value={"value": token}) as request, \
                patch("auth.command", return_value="") as run, redirect_stdout(output):
            auth.login("build")
        sent = request.call_args.args[0]
        self.assertEqual(parse_qs(urlsplit(sent.full_url).query)["audience"], [auth.AUDIENCE])
        self.assertEqual(sent.headers["Authorization"], "bearer synthetic-runner-token")
        self.assertEqual([args.args[0][:2] for args in run.call_args_list], [["az", "cloud"], ["az", "login"], ["az", "account"]])
        self.assertEqual([args.kwargs["timeout"] for args in run.call_args_list], [30, 60, 30])
        self.assertIn("--federated-token", run.call_args_list[1].args[0])
        self.assertNotIn(token, output.getvalue())
        self.assertNotIn(SUBSCRIPTION, output.getvalue())

    def test_oidc_endpoint_cannot_redirect_credentials_to_other_hosts(self):
        for endpoint in ("http://oidc.actions.githubusercontent.com/token", "https://actions.githubusercontent.com.evil.invalid/token",
                         "https://oidc.actions.githubusercontent.com:8443/token", "https://user@oidc.actions.githubusercontent.com/token"):
            with self.subTest(endpoint=endpoint), patch.dict(os.environ, settings() | {"ACTIONS_ID_TOKEN_REQUEST_URL": endpoint}, clear=True), \
                    patch("auth.prepare"), patch("auth.authorize", return_value=SOURCE), patch("auth.private_request") as request, \
                    self.assertRaises(common.Refusal):
                auth.login("build")
            request.assert_not_called()


class ImageTests(OfflineCase):
    @staticmethod
    def context_rules():
        rules = ["**", "!global.json", "!Directory.Build.props", "!Directory.Packages.props", "!src/", "src/*"]
        for project in ("Core", "Agents", "Infrastructure", "ServiceDefaults", "Web", "Mcp"):
            rules += [f"!src/LessonPlanner.{project}/", f"!src/LessonPlanner.{project}/**"]
        return rules + ["**/bin/", "**/obj/", "**/Properties/", "**/.*", "**/*.user", "**/appsettings.*.json",
                        "**/*secrets*", "**/*.key", "**/*.pfx", "**/*.p12", "**/*.pem", "**/*.log", "**/*.trx"]

    def test_context_rejects_extra_includes_and_reopened_private_files(self):
        rules = self.context_rules()
        with patch.object(Path, "read_text", return_value="\n".join(rules)), patch.object(Path, "exists", return_value=False):
            images.check_context()
        for changed in (rules + ["!.azure/**"], rules + ["!src/LessonPlanner.Web/**"], rules[1:], rules[:-1]):
            with self.subTest(changed=changed[-1]), patch.object(Path, "read_text", return_value="\n".join(changed)), \
                    patch.object(Path, "exists", return_value=False), self.assertRaises(common.Refusal):
                images.check_context()

    def test_context_requires_the_src_children_exclusion(self):
        rules = [rule for rule in self.context_rules() if rule != "src/*"]
        with patch.object(Path, "read_text", return_value="\n".join(rules)), \
                patch.object(Path, "exists", return_value=False), self.assertRaises(common.Refusal):
            images.check_context()

    def test_context_requires_src_exclusion_before_every_child_inclusion(self):
        rules = [rule for rule in self.context_rules() if rule != "src/*"]
        misplaced = [rules[:index] + ["src/*"] + rules[index:] for index in (
            rules.index("!src/"), rules.index("!src/LessonPlanner.Core/") + 1,
            rules.index("!src/LessonPlanner.Core/**") + 1, rules.index("**/bin/"))]
        duplicated = [self.context_rules() + ["!src/"], self.context_rules() + ["src/*"]]
        for index, changed in enumerate(misplaced + duplicated):
            with self.subTest(case=index), patch.object(Path, "read_text", return_value="\n".join(changed)), \
                    patch.object(Path, "exists", return_value=False), self.assertRaises(common.Refusal):
                images.check_context()

    def test_per_dockerfile_context_override_is_rejected(self):
        with patch.object(Path, "read_text", return_value="\n".join(self.context_rules())), \
                patch.object(Path, "exists", return_value=True), self.assertRaises(common.Refusal):
            images.check_context()

    def test_native_amd64_host_and_daemon_are_required(self):
        with patch("images.platform.system", return_value="Linux"), patch("images.platform.machine", return_value="x86_64"), \
                patch("images.command", return_value=json.dumps({"OSType": "linux", "Architecture": "x86_64"})):
            images.native_amd64()
        for machine in ("aarch64", "arm64"):
            with self.subTest(machine=machine), patch("images.platform.system", return_value="Linux"), \
                    patch("images.platform.machine", return_value=machine), patch("images.command") as run, self.assertRaises(common.Refusal):
                images.native_amd64()
            run.assert_not_called()
        with patch("images.platform.system", return_value="Linux"), patch("images.platform.machine", return_value="x86_64"), \
                patch("images.command", return_value=json.dumps({"OSType": "linux", "Architecture": "aarch64"})), self.assertRaises(common.Refusal):
            images.native_amd64()

    def test_image_platform_user_port_and_entrypoint_are_verified(self):
        with patch("images.command", return_value=json.dumps([image_fixture()])):
            self.assertEqual(images.inspect_local("web", SOURCE), OLD_DIGEST)
        for keys, value in ((("Architecture",), "arm64"), (("Config", "User"), "0"), (("Config", "User"), "root"),
                            (("Config", "ExposedPorts"), {"80/tcp": {}}), (("Config", "Entrypoint"), ["other"]),
                            (("Config", "Cmd"), ["--urls", "http://+:80"]),
                            (("Config", "Env"), ["ASPNETCORE_HTTP_PORTS=8080", "ASPNETCORE_ENVIRONMENT=Testing"]),
                            (("Config", "Labels", "org.opencontainers.image.revision"), "b" * 40)):
            candidate = image_fixture()
            change(candidate, keys, value)
            with self.subTest(keys=keys, value=value), patch("images.command", return_value=json.dumps([candidate])), self.assertRaises(common.Refusal):
                images.inspect_local("web", SOURCE)

    def test_builds_use_native_builder_root_context_no_credentials_or_rid(self):
        with patch("images.native_amd64") as native, patch("images.check_context") as context_check, \
                patch("images.command", return_value="") as run, patch("images.inspect_local") as inspect, patch("images.startup") as startup:
            images.build_images(SOURCE)
        native.assert_called_once()
        context_check.assert_called_once()
        self.assertEqual(inspect.call_count, 2)
        self.assertEqual(startup.call_count, 2)
        for request in run.call_args_list:
            arguments = request.args[0]
            self.assertEqual(arguments[:3], ["docker", "buildx", "build"])
            self.assertEqual(arguments[arguments.index("--platform") + 1], "linux/amd64")
            self.assertEqual(arguments[arguments.index("--builder") + 1], "default")
            self.assertEqual(arguments[-1], ".")
            self.assertNotIn("--build-arg", arguments)
            self.assertNotIn("--secret", arguments)
            self.assertNotIn("--push", arguments)
            self.assertEqual(request.kwargs["timeout"], 900)

    def test_unprivileged_validation_needs_no_release_context(self):
        with patch.dict(os.environ, {}, clear=True), patch("images.command", return_value=SOURCE), patch("images.build_images") as build:
            images.validate()
        build.assert_called_once_with(SOURCE)

    def test_registry_tokens_only_flow_to_captured_request_and_stdin(self):
        with patch.dict(os.environ, settings(), clear=True), \
                patch("images.command", side_effect=["synthetic-access-token", ""]) as run, \
                patch("images.private_request", return_value={"refresh_token": "synthetic-refresh-token"}) as request:
            images.registry_login(SERVER)
        body = parse_qs(request.call_args.args[0].data.decode())
        self.assertEqual(body["access_token"], ["synthetic-access-token"])
        self.assertEqual(request.call_args.args[0].full_url, f"https://{SERVER}/oauth2/exchange")
        self.assertIn("https://containerregistry.azure.net", run.call_args_list[0].args[0])
        self.assertEqual(run.call_args_list[1].kwargs["stdin"], "synthetic-refresh-token\n")
        self.assertEqual(run.call_args_list[1].kwargs["timeout"], 60)
        self.assertIn("--password-stdin", run.call_args_list[1].args[0])
        self.assertNotIn("synthetic-refresh-token", run.call_args_list[1].args[0])

    def test_immutable_manifest_is_bound_to_validated_image_configuration(self):
        reference = f"{SERVER}/lumero-web:fixture"
        metadata = [{"RepoDigests": [f"{SERVER}/lumero-web@{DIGESTS['web']}"]}]
        manifest = {"schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json", "config": {"digest": OLD_DIGEST}}
        with patch("images.command", side_effect=[json.dumps(metadata), json.dumps(manifest)]) as run:
            self.assertEqual(images.pushed_digest(reference, OLD_DIGEST), DIGESTS["web"])
        self.assertEqual(run.call_args_list[-1],
                         call(["docker", "manifest", "inspect", f"{SERVER}/lumero-web@{DIGESTS['web']}"], timeout=60))
        for changed in (manifest | {"manifests": []}, manifest | {"config": {"digest": DIGESTS["mcp"]}}):
            with self.subTest(changed=changed), \
                    patch("images.command", side_effect=[json.dumps(metadata), json.dumps(changed)]), self.assertRaises(common.Refusal):
                images.pushed_digest(reference, OLD_DIGEST)

    def test_push_keeps_repository_provenance_and_operation_timeouts(self):
        with patch.dict(os.environ, settings(), clear=True), patch("common.command", return_value=SOURCE) as head, \
                patch("images.inspect_local", return_value=OLD_DIGEST), patch("images.registry_login"), \
                patch("images.command", return_value="") as run, \
                patch("images.pushed_digest", side_effect=[DIGESTS["web"], DIGESTS["mcp"]]) as digest, \
                patch("images.record") as evidence:
            images.push()
        head.assert_called_once_with(["git", "rev-parse", "HEAD"])
        expected_commands, expected_digests, expected_evidence = [], [], []
        for kind in ("web", "mcp"):
            reference = f"{SERVER}/lumero-{kind}:sha-{SOURCE}-run-1-attempt-1"
            expected_commands += [call(["docker", "image", "tag", images.local_image(kind, SOURCE), reference]),
                                  call(["docker", "push", reference], timeout=600)]
            expected_digests.append(call(reference, OLD_DIGEST))
            expected_evidence.append(call("pushed", SOURCE, kind, DIGESTS[kind]))
        self.assertEqual(run.call_args_list, expected_commands)
        self.assertEqual(digest.call_args_list, expected_digests)
        self.assertEqual(evidence.call_args_list, expected_evidence)

    def test_failed_push_stops_without_retry_or_further_mutation(self):
        with patch.dict(os.environ, settings(), clear=True), patch("images.authorize", return_value=SOURCE), \
                patch("images.inspect_local", return_value=OLD_DIGEST), patch("images.registry_login"), \
                patch("images.command", side_effect=["", common.Refusal("Push failed.")]) as run, \
                patch("images.pushed_digest") as digest, patch("images.record") as evidence, self.assertRaises(common.Refusal):
            images.push()
        reference = f"{SERVER}/lumero-web:sha-{SOURCE}-run-1-attempt-1"
        self.assertEqual(run.call_args_list, [call(["docker", "image", "tag", images.local_image("web", SOURCE), reference]),
                                              call(["docker", "push", reference], timeout=600)])
        digest.assert_not_called()
        evidence.assert_not_called()


class StartupTests(OfflineCase):
    def test_synthetic_quiz_origin_avoids_prohibited_placeholder_suffixes(self):
        for kind in ("web", "mcp"):
            with self.subTest(kind=kind):
                origin = urlsplit(smoke.configuration(kind)["Quiz__PublicBaseUrl"])
                self.assertEqual(origin.scheme, "https")
                self.assertTrue(origin.hostname.endswith(".test"))
                self.assertFalse(origin.hostname.endswith((".invalid", ".example")))
                self.assertIn(origin.path, ("", "/"))
                self.assertFalse(origin.username or origin.query or origin.fragment)

    def test_probe_host_matches_the_exact_synthetic_https_origin(self):
        for kind in ("web", "mcp"):
            with self.subTest(kind=kind):
                configuration = smoke.configuration(kind)
                host = urlsplit(configuration["Quiz__PublicBaseUrl"]).hostname
                self.assertEqual(configuration["AllowedHosts"], host)
                self.assertIn(f"Host: {host}", smoke.LIVENESS)
                self.assertNotIn("X-Forwarded-", smoke.LIVENESS)
                self.assertFalse(any("KnownNetworks" in key or "KnownProxies" in key for key in configuration))
        self.assertEqual(smoke.configuration("web")["ReverseProxy__UseFixedHttpsOrigin"], "true")
        self.assertNotIn("ReverseProxy__UseFixedHttpsOrigin", smoke.configuration("mcp"))

    def test_created_container_is_removed_when_start_fails(self):
        container_id = "c" * 64
        with patch("smoke.command", side_effect=[container_id, common.Refusal("start failed"), ""]) as run, \
                self.assertRaises(common.Refusal):
            smoke.startup("web", "lumero-web:" + SOURCE)
        self.assertEqual(run.call_args_list[0].args[0][:2], ["docker", "create"])
        self.assertEqual(run.call_args_list[1].args[0], ["docker", "start", container_id])
        self.assertEqual(run.call_args_list[-1].args[0], ["docker", "rm", "--force", container_id])

    def test_synthetic_configuration_retains_production_managed_identity_contract(self):
        web, mcp = smoke.configuration("web"), smoke.configuration("mcp")
        self.assertEqual(web["ASPNETCORE_ENVIRONMENT"], "Production")
        self.assertEqual(web["AzureAd__ClientCredentials__0__SourceType"], "SignedAssertionFromManagedIdentity")
        self.assertNotEqual(web["AzureAd__ClientId"], mcp["Mcp__Audience"])
        self.assertTrue(urlsplit(web["Foundry__Endpoint"]).hostname.endswith(".services.ai.azure.com"))
        self.assertIn(".invalid.", web["Foundry__Endpoint"])
        self.assertNotIn("/health/ready", smoke.LIVENESS)
        self.assertNotIn("Authorization", smoke.LIVENESS)

    def test_startup_is_loopback_only_nonroot_and_removes_only_its_container(self):
        container_id = "c" * 64
        with patch("smoke.command", side_effect=[container_id, "", "1654\n", '{"Running":true}', "", ""]) as run:
            smoke.startup("web", "lumero-web:" + SOURCE)
        arguments = run.call_args_list[0].args[0]
        self.assertEqual(arguments[arguments.index("--network") + 1], "none")
        self.assertEqual(arguments[arguments.index("--pull") + 1], "never")
        self.assertIn("--read-only", arguments)
        self.assertNotIn("--publish", arguments)
        self.assertNotIn("--volume", arguments)
        self.assertNotIn("--user", arguments)
        self.assertIn(smoke.LIVENESS, run.call_args_list[-2].args[0])
        self.assertEqual(run.call_args_list[-1].args[0], ["docker", "rm", "--force", container_id])

    def test_root_or_exited_container_is_not_a_passing_startup(self):
        container_id = "c" * 64
        for replies in ([container_id, "", "0", ""], [container_id, "", "1654", '{"Running":false}', ""]):
            with self.subTest(replies=replies), patch("smoke.command", side_effect=replies) as run, self.assertRaises(common.Refusal):
                smoke.startup("mcp", "lumero-mcp:" + SOURCE)
            self.assertEqual(run.call_args_list[-1].args[0], ["docker", "rm", "--force", container_id])

    def test_startup_failure_is_bounded(self):
        container_id = "c" * 64
        with patch("smoke.command", side_effect=[container_id, "", "1654", '{"Running":true}', common.Refusal("not ready"), ""]) as run, \
                patch("smoke.time.monotonic", side_effect=[0, 0, 0, 2]), patch("smoke.time.sleep") as sleep, self.assertRaises(common.Refusal):
            smoke.startup("web", "lumero-web:" + SOURCE, seconds=1)
        sleep.assert_called_once_with(1)
        self.assertEqual(run.call_args_list[-1].args[0], ["docker", "rm", "--force", container_id])


class TestEvidenceTests(OfflineCase):
    @staticmethod
    def trx():
        return ET.fromstring('<TestRun xmlns="urn:ci"><Results><UnitTestResult outcome="Passed" /></Results>'
                             '<ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" '
                             'error="0" aborted="0" notExecuted="0" /></ResultSummary></TestRun>')

    def test_executed_case_is_required(self):
        self.assertEqual(verify_trx.verify(self.trx()), 1)
        for field, value in (("total", "0"), ("executed", "0"), ("passed", "0"), ("failed", "1"),
                             ("total", "2"), ("error", "1"), ("aborted", "1"), ("notExecuted", "1")):
            candidate = self.trx()
            candidate.find(".//{*}Counters").set(field, value)
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                verify_trx.verify(candidate)

    def test_missing_or_nonpassing_result_and_summary_fail(self):
        for path, key, value in ((".//{*}ResultSummary", "outcome", "Aborted"), (".//{*}UnitTestResult", "outcome", "NotExecuted")):
            candidate = self.trx()
            candidate.find(path).set(key, value)
            with self.subTest(path=path), self.assertRaises(ValueError):
                verify_trx.verify(candidate)
        with self.assertRaises(ValueError):
            verify_trx.verify(ET.fromstring("<TestRun />"))
        with self.assertRaises(ValueError):
            verify_trx.main([])
        with patch("verify_trx.ET.parse", side_effect=FileNotFoundError), self.assertRaises(FileNotFoundError):
            verify_trx.main(["missing.trx"])

    def test_all_three_expected_trx_files_are_required(self):
        paths = ["unit.trx", "probe.trx", "handler-cancellation.trx"]
        with patch("verify_trx.ET.parse", return_value=ET.ElementTree(self.trx())) as parse:
            verify_trx.main(paths)
        self.assertEqual(parse.call_args_list, [call(Path(path)) for path in paths])
        for index, path in enumerate(paths):
            results = [ET.ElementTree(self.trx()) for _ in paths]
            results[index] = FileNotFoundError()
            with self.subTest(path=path), patch("verify_trx.ET.parse", side_effect=results), self.assertRaises(FileNotFoundError):
                verify_trx.main(paths)

    def test_empty_handler_trx_cannot_be_hidden_by_passing_unit_and_probe_results(self):
        empty = ET.fromstring('<TestRun xmlns="urn:ci"><Results /><ResultSummary outcome="Completed">'
                              '<Counters total="0" executed="0" passed="0" failed="0" /></ResultSummary></TestRun>')
        results = [ET.ElementTree(self.trx()), ET.ElementTree(self.trx()), ET.ElementTree(empty)]
        with patch("verify_trx.ET.parse", side_effect=results), self.assertRaises(ValueError):
            verify_trx.main(["unit.trx", "probe.trx", "handler-cancellation.trx"])


class ReleaseTests(OfflineCase):
    @staticmethod
    def workload_drift_cases():
        template = ("properties", "template")
        resources = template + ("containers", 0, "resources")
        return [
            (("properties", "workloadProfileName"), "Dedicated"),
            (("properties", "workloadProfileName"), None),
            (template + ("scale",), None), (template + ("scale",), {}),
            (template + ("scale", "minReplicas"), 0), (template + ("scale", "minReplicas"), 2),
            (template + ("scale", "maxReplicas"), 0), (template + ("scale", "maxReplicas"), 2),
            (template + ("scale", "minReplicas"), True), (template + ("scale", "maxReplicas"), "1"),
            (template + ("scale", "rules"), [{"name": "unapproved"}]),
            (template + ("scale", "rules"), False),
            (resources, None), (resources, {}), (resources + ("cpu",), 0.5),
            (resources + ("cpu",), True), (resources + ("cpu",), "1"),
            (resources + ("memory",), "1Gi"), (resources + ("memory",), None),
            (resources + ("gpu",), 1),
        ]

    def test_existing_pair_matches_contract_without_modification(self):
        web, mcp = app_fixture("web"), app_fixture("mcp")
        original = deepcopy((web, mcp))
        for kind, app in (("web", web), ("mcp", mcp)):
            self.assertEqual(app["properties"]["workloadProfileName"], "Consumption")
            self.assertEqual(app["properties"]["template"]["scale"], {"minReplicas": 1, "maxReplicas": 1, "rules": []})
            self.assertEqual(release.container(app)["resources"], {"cpu": 1, "memory": "2Gi"} if kind == "web"
                             else {"cpu": 0.25, "memory": "0.5Gi"})
            release.validate_app(app, app_id(kind), kind, SERVER)
        release.validate_pair(web, mcp)
        self.assertEqual((web, mcp), original)

    def test_tcp_startup_and_http_health_routes_are_required(self):
        for kind in ("web", "mcp"):
            probes = release.container(app_fixture(kind))["probes"]
            changes = [
                ((), probes[1:]), ((), probes + [deepcopy(probes[0])]), ((), [None, None, None]),
                ((0, "type"), "Liveness"), ((0, "type"), "Unknown"),
                ((0,), {"type": "Startup", "httpGet": {"path": "/health/live", "port": 8080}}),
                ((0, "tcpSocket"), None), ((0, "tcpSocket", "port"), 80),
                ((0, "tcpSocket", "host"), "outside-fixture.invalid"),
                ((0, "httpGet"), {"path": "/health/live", "port": 8080}),
                ((0, "exec"), {"command": ["true"]}),
                ((1,), {"type": "Liveness", "tcpSocket": {"port": 8080}}),
                ((1, "httpGet", "path"), "/health/ready"), ((2, "httpGet", "path"), "/health/live"),
                ((1, "httpGet", "port"), 80), ((2, "httpGet", "scheme"), "HTTPS"),
                ((1, "tcpSocket"), {"port": 8080}), ((2, "exec"), {"command": ["true"]}),
            ]
            for keys, value in changes:
                candidate = app_fixture(kind)
                change(candidate, ("properties", "template", "containers", 0, "probes") + keys, value)
                with self.subTest(kind=kind, keys=keys, value=value), self.assertRaises(common.Refusal):
                    release.validate_app(candidate, app_id(kind), kind, SERVER)

    def test_numeric_cpu_and_empty_optional_scale_rules_are_supported(self):
        for kind in ("web", "mcp"):
            for rules in (None, []):
                candidate = app_fixture(kind)
                resources = release.container(candidate)["resources"]
                resources["cpu"] = float(resources["cpu"])
                candidate["properties"]["template"]["scale"]["rules"] = rules
                with self.subTest(kind=kind, rules=rules):
                    release.validate_app(candidate, app_id(kind), kind, SERVER)

    def test_out_of_scope_workloads_prevent_any_mutation(self):
        for kind in ("web", "mcp"):
            for keys, value in self.workload_drift_cases():
                apps = {app: app_fixture(app) for app in ("mcp", "web")}
                change(apps[kind], keys, value)
                with self.subTest(kind=kind, keys=keys, value=value):
                    events, completed = self.run_release(apps)
                    self.assertFalse(completed)
                    self.assertEqual(events, [("mcp", "GET"), ("web", "GET")])

    def test_unexpected_existing_topology_or_auth_fails_closed(self):
        prefix = ("properties", "template", "containers", 0)
        cases = [
            (("identity", "type"), "SystemAssigned"),
            (("properties", "provisioningState"), "InProgress"),
            (("properties", "latestReadyRevisionName"), "older"),
            (("properties", "configuration", "activeRevisionsMode"), "Multiple"),
            (("properties", "configuration", "ingress", "targetPort"), 80),
            (("properties", "configuration", "ingress", "external"), False),
            (("properties", "configuration", "ingress", "allowInsecure"), True),
            (("properties", "configuration", "ingress", "traffic"), [{"weight": 100, "revisionName": "pinned"}]),
            (("properties", "configuration", "registries"), [{"server": SERVER, "username": "admin"}]),
            (("properties", "configuration", "dapr"), {"enabled": True}),
            (("properties", "template", "revisionSuffix"), "fixed"),
            (("properties", "template", "initContainers"), [{}]),
            (("properties", "template", "serviceBinds"), [{}]),
            (("properties", "template", "containers"), [{}, {}]),
            (prefix + ("image",), f"{SERVER}/lumero-web:latest"),
            (prefix + ("probes",), []),
            (prefix + ("probes", 1, "httpGet", "path"), "/invoke-model"),
        ]
        for keys, replacement in cases:
            candidate = app_fixture("web")
            change(candidate, keys, replacement)
            with self.subTest(keys=keys), self.assertRaises(common.Refusal):
                release.validate_app(candidate, app_id("web"), "web", SERVER)
        for name, value in (("AzureAd__ClientSecret", "synthetic"), ("aspnetcore_environment", "Testing"),
                            ("azuread__clientcredentials__1__sourcetype", "Path")):
            candidate = app_fixture("web")
            release.container(candidate)["env"].append({"name": name, "value": value})
            with self.subTest(name=name), self.assertRaises(common.Refusal):
                release.validate_app(candidate, app_id("web"), "web", SERVER)

    def test_oidc_and_mcp_audiences_cannot_be_merged_or_hidden_in_secret_refs(self):
        for key, value in (("AzureAd__ClientId", smoke.configuration("mcp")["Mcp__Audience"]),
                           ("AzureAd__TenantId", str(UUID(int=77))), ("Mcp__CallerClientId", str(UUID(int=78))),
                           ("Mcp__TokenScope", "api://wrong/.default"), ("AzureAd__ClientId", str(UUID(int=0))),
                           ("AzureAd__ClientId", None)):
            web = app_fixture("web")
            set_env(web, key, value)
            with self.subTest(key=key, value=value), self.assertRaises(common.Refusal):
                release.validate_pair(web, app_fixture("mcp"))

    def test_managed_identity_exchange_audience_is_not_the_mcp_scope(self):
        key = "AzureAd__ClientCredentials__0__TokenExchangeUrl"
        for value in (smoke.configuration("web")["Mcp__TokenScope"], "https://other.invalid", "", None):
            candidate = app_fixture("web")
            set_env(candidate, key, value)
            with self.subTest(value=value), self.assertRaises(common.Refusal):
                release.validate_app(candidate, app_id("web"), "web", SERVER)
        candidate = app_fixture("web")
        release.container(candidate)["env"] = [entry for entry in release.container(candidate)["env"] if entry["name"] != key]
        release.validate_app(candidate, app_id("web"), "web", SERVER)

    def test_image_patch_preserves_every_other_container_field_and_input(self):
        app = app_fixture("web")
        release.container(app)["env"].append({"name": "OpaqueSetting", "secretRef": "synthetic-reference"})
        original = deepcopy(app)
        desired = f"{SERVER}/lumero-web@{DIGESTS['web']}"
        result = release.image_patch(app, desired)
        replacement = deepcopy(release.container(app))
        replacement["image"] = desired
        self.assertEqual(result, {"location": app["location"], "properties": {"template": {"containers": [replacement]}}})
        self.assertEqual(app, original)

    def test_image_patch_omits_server_only_ephemeral_storage(self):
        for kind, storage in (("web", "4Gi"), ("mcp", "1Gi")):
            app = app_fixture(kind)
            release.container(app)["resources"]["ephemeralStorage"] = storage
            original = deepcopy(app)
            desired = f"{SERVER}/lumero-{kind}@{DIGESTS[kind]}"
            result = release.image_patch(app, desired)
            replacement = deepcopy(release.container(app))
            replacement["resources"].pop("ephemeralStorage")
            replacement["image"] = desired
            with self.subTest(kind=kind):
                self.assertEqual(result, {"location": app["location"], "properties": {"template": {"containers": [replacement]}}})
                self.assertEqual(app, original)

    def test_non_image_comparison_ignores_only_documented_server_state(self):
        original = app_fixture("web")
        changed = deepcopy(original)
        changed["etag"] = "new"
        changed["properties"]["latestRevisionName"] = "new"
        release.container(changed)["image"] = f"{SERVER}/lumero-web@{DIGESTS['web']}"
        self.assertEqual(release.non_image_settings(original), release.non_image_settings(changed))
        changed["properties"]["environmentId"] = "changed-environment"
        self.assertNotEqual(release.non_image_settings(original), release.non_image_settings(changed))

    def test_arm_only_get_patch_and_exact_revision_get(self):
        app = app_fixture("web")
        body = release.image_patch(app, f"{SERVER}/lumero-web@{DIGESTS['web']}")
        with patch.dict(os.environ, settings(), clear=True), patch("release.command", return_value=json.dumps(app)) as run:
            release.rest(app_id("web"))
            release.rest(app_id("web"), method="PATCH", body=body)
            release.rest(app_id("web"), revision="web-fixture--old")
        for request, method in zip(run.call_args_list, ("GET", "PATCH", "GET")):
            arguments = request.args[0]
            self.assertEqual(arguments[:4], ["az", "rest", "--method", method])
            self.assertTrue(arguments[arguments.index("--url") + 1].startswith(release.ARM + app_id("web")))
            self.assertEqual(request.kwargs["timeout"], 60)
        self.assertIn("--output", run.call_args_list[1].args[0])
        self.assertEqual(run.call_args_list[1].args[0][-1], json.dumps(body, separators=(",", ":")))
        self.assertIn("/revisions/web-fixture--old?api-version=2025-01-01", run.call_args_list[2].args[0][5])

    def test_arm_refuses_other_methods_and_paths(self):
        for kwargs in ({"method": "PUT", "body": {}}, {"method": "POST", "body": {}}, {"method": "DELETE"},
                       {"revision": "../environment"}, {"method": "GET", "body": {}}, {"method": "PATCH"}):
            with self.subTest(kwargs=kwargs), patch.dict(os.environ, settings(), clear=True), patch("release.command") as run, \
                    self.assertRaises(common.Refusal):
                release.rest(app_id("web"), **kwargs)
            run.assert_not_called()

    def test_arm_timeouts_stay_capped_and_failed_mutations_are_not_retried(self):
        for seconds, expected in ((60, 60), (12, 12), (120, 60)):
            with self.subTest(seconds=seconds), patch.dict(os.environ, settings(), clear=True), \
                    patch("release.command", return_value="{}") as run:
                release.rest(app_id("web"), timeout=seconds)
            run.assert_called_once()
            self.assertEqual(run.call_args.kwargs["timeout"], expected)
        with patch.dict(os.environ, settings(), clear=True), \
                patch("release.command", side_effect=common.Refusal("PATCH failed.")) as run, self.assertRaises(common.Refusal):
            release.rest(app_id("web"), method="PATCH", body={})
        run.assert_called_once()
        self.assertEqual(run.call_args.args[0][:4], ["az", "rest", "--method", "PATCH"])
        self.assertEqual(run.call_args.kwargs["timeout"], 60)

    def run_release(self, apps, *, drift=False, unready=False, workload_change=None):
        events = []
        counts = {key: 0 for key in apps}
        def request(identifier, method="GET", body=None, **kwargs):
            kind = "web" if identifier == app_id("web") else "mcp"
            events.append((kind, method))
            if method == "PATCH":
                apps[kind]["properties"]["template"]["containers"] = deepcopy(body["properties"]["template"]["containers"])
                return None
            counts[kind] += 1
            result = deepcopy(apps[kind])
            if drift and kind == "web" and counts[kind] == 2:
                result["tags"]["fixture"] = "concurrent-writer"
            if unready and counts[kind] == 2:
                result["properties"]["latestReadyRevisionName"] = "older-than-preflight"
            if workload_change is not None and kind == workload_change[0] and counts[kind] == 2:
                change(result, workload_change[1], workload_change[2])
            return result
        with patch.dict(os.environ, settings(), clear=True), patch("common.command", return_value=SOURCE), \
                patch("release.rest", side_effect=request), patch("release.wait_ready"), patch("release.record"):
            try:
                release.release()
            except common.Refusal:
                return events, False
        return events, True

    def test_both_apps_preflight_before_mcp_then_web_mutations(self):
        events, completed = self.run_release({kind: app_fixture(kind) for kind in ("mcp", "web")})
        self.assertTrue(completed)
        self.assertEqual(events, [("mcp", "GET"), ("web", "GET"), ("mcp", "GET"), ("mcp", "PATCH"), ("web", "GET"), ("web", "PATCH")])

    def test_invalid_second_app_prevents_any_mutation(self):
        apps = {kind: app_fixture(kind) for kind in ("mcp", "web")}
        apps["web"]["properties"]["configuration"]["activeRevisionsMode"] = "Multiple"
        events, completed = self.run_release(apps)
        self.assertFalse(completed)
        self.assertEqual(events, [("mcp", "GET"), ("web", "GET")])

    def test_concurrent_drift_stops_without_rolling_back_first_app(self):
        events, completed = self.run_release({kind: app_fixture(kind) for kind in ("mcp", "web")}, drift=True)
        self.assertFalse(completed)
        self.assertEqual([event for event in events if event[1] == "PATCH"], [("mcp", "PATCH")])

    def test_app_must_still_be_ready_immediately_before_mutation(self):
        events, completed = self.run_release({kind: app_fixture(kind) for kind in ("mcp", "web")}, unready=True)
        self.assertFalse(completed)
        self.assertEqual(events, [("mcp", "GET"), ("web", "GET"), ("mcp", "GET")])

    def test_workload_drift_is_refused_immediately_before_its_mutation(self):
        for kind in ("web", "mcp"):
            for keys, value in self.workload_drift_cases():
                apps = {app: app_fixture(app) for app in ("mcp", "web")}
                with self.subTest(kind=kind, keys=keys, value=value):
                    events, completed = self.run_release(apps, workload_change=(kind, keys, value))
                    self.assertFalse(completed)
                    expected = [("mcp", "GET"), ("web", "GET"), ("mcp", "GET")]
                    if kind == "web":
                        expected += [("mcp", "PATCH"), ("web", "GET")]
                    self.assertEqual(events, expected)

    def test_matching_digests_are_noops_not_forced_revisions(self):
        apps = {kind: app_fixture(kind) for kind in ("mcp", "web")}
        for kind, app in apps.items():
            release.container(app)["image"] = f"{SERVER}/lumero-{kind}@{DIGESTS[kind]}"
        events, completed = self.run_release(apps)
        self.assertTrue(completed)
        self.assertFalse(any(method == "PATCH" for _, method in events))

    def test_readiness_requires_exact_active_healthy_running_revision(self):
        before = app_fixture("web")
        current = deepcopy(before)
        desired = f"{SERVER}/lumero-web@{DIGESTS['web']}"
        release.container(current)["image"] = desired
        with patch("release.rest", side_effect=[current, revision_fixture(current)]) as request:
            release.wait_ready(app_id("web"), before, desired, "web", SERVER)
        self.assertEqual(request.call_count, 2)
        self.assertEqual(request.call_args_list[1].kwargs["revision"], "web-fixture--old")

    def test_readiness_accepts_healthy_revision_at_maximum_scale(self):
        for kind in ("web", "mcp"):
            before = app_fixture(kind)
            current = deepcopy(before)
            desired = f"{SERVER}/lumero-{kind}@{DIGESTS[kind]}"
            release.container(current)["image"] = desired
            revision = revision_fixture(current)
            revision["properties"]["runningState"] = "RunningAtMaxScale"
            with self.subTest(kind=kind), patch("release.time.monotonic", side_effect=[0, 0, 0, 0, 1, 1]), \
                    patch("release.rest", side_effect=[current, revision]) as request, patch("release.time.sleep") as sleep:
                release.wait_ready(app_id(kind), before, desired, kind, SERVER, seconds=1)
            self.assertEqual(request.call_count, 2)
            sleep.assert_not_called()

    @staticmethod
    def platform_readiness_fixture(kind, *, omitted=False):
        before = app_fixture(kind)
        template = before["properties"]["template"]
        template["revisionSuffix"] = ""
        template["scale"].update({"cooldownPeriod": 300, "pollingInterval": 30})
        release.container(before)["resources"]["ephemeralStorage"] = "4Gi" if kind == "web" else "1Gi"
        current = deepcopy(before)
        desired = f"{SERVER}/lumero-{kind}@{DIGESTS[kind]}"
        release.container(current)["image"] = desired
        revision = revision_fixture(current)
        revision["properties"]["runningState"] = "RunningAtMaxScale"
        actual = revision["properties"]["template"]
        actual["containers"][0]["resources"].pop("ephemeralStorage")
        for settings, key in ((actual, "revisionSuffix"), (actual["scale"], "cooldownPeriod"),
                              (actual["scale"], "pollingInterval")):
            if omitted:
                settings.pop(key)
            else:
                settings[key] = None
        return before, current, revision, desired

    def test_readiness_accepts_platform_template_representations(self):
        for kind in ("web", "mcp"):
            for omitted in (False, True):
                before, current, revision, desired = self.platform_readiness_fixture(kind, omitted=omitted)
                original = deepcopy((before, current, revision))
                with self.subTest(kind=kind, omitted=omitted), patch("release.rest", side_effect=[current, revision]) as request:
                    release.wait_ready(app_id(kind), before, desired, kind, SERVER)
                    self.assertEqual(request.call_count, 2)
                    self.assertEqual((before, current, revision), original)

    def test_revision_template_comparison_preserves_configuration_drift_rejection(self):
        cases = [
            (("revisionSuffix",), "unapproved"), (("scale", "cooldownPeriod"), 301),
            (("scale", "pollingInterval"), 31), (("scale", "maxReplicas"), 2),
            (("containers", 0, "resources", "cpu"), 2), (("containers", 0, "resources", "memory"), "8Gi"),
            (("containers", 0, "resources", "gpu"), 1), (("containers", 0, "name"), "unapproved"),
            (("containers", 0, "image"), "unapproved"), (("containers", 0, "env", 0, "value"), "unapproved"),
            (("unrecognisedSetting",), "unapproved"),
        ]
        for kind in ("web", "mcp"):
            for keys, value in cases:
                before, current, revision, desired = self.platform_readiness_fixture(kind)
                change(revision, ("properties", "template") + keys, value)
                with self.subTest(kind=kind, keys=keys), patch("release.rest", side_effect=[current, revision]) as request, \
                        self.assertRaises(common.Refusal):
                    release.wait_ready(app_id(kind), before, desired, kind, SERVER)
                self.assertEqual(request.call_count, 2)

    def test_maximum_scale_still_requires_active_healthy_provisioned_revision(self):
        before = app_fixture("web")
        current = deepcopy(before)
        desired = f"{SERVER}/lumero-web@{DIGESTS['web']}"
        release.container(current)["image"] = desired
        for key, value in (("active", False), ("healthState", "Unknown"), ("provisioningState", "Provisioning")):
            revision = revision_fixture(current)
            revision["properties"]["runningState"] = "RunningAtMaxScale"
            revision["properties"][key] = value
            with self.subTest(key=key), patch("release.time.monotonic", side_effect=[0, 0, 0, 0, 1, 1]), \
                    patch("release.rest", side_effect=[current, revision]), patch("release.time.sleep"), \
                    self.assertRaises(common.Refusal):
                release.wait_ready(app_id("web"), before, desired, "web", SERVER, seconds=1)

    def test_failed_or_mismatched_revision_is_not_accepted(self):
        before = app_fixture("web")
        current = deepcopy(before)
        desired = f"{SERVER}/lumero-web@{DIGESTS['web']}"
        release.container(current)["image"] = desired
        for keys, value in ((("properties", "provisioningState"), "Failed"), (("properties", "healthState"), "Unhealthy"),
                            (("properties", "runningState"), "Stopped"), (("properties", "template", "scale", "minReplicas"), 99),
                            (("id",), "wrong-app")):
            revision = revision_fixture(current)
            change(revision, keys, value)
            with self.subTest(keys=keys), patch("release.rest", side_effect=[current, revision]), self.assertRaises(common.Refusal):
                release.wait_ready(app_id("web"), before, desired, "web", SERVER)

    def test_readiness_requests_and_sleeps_use_remaining_deadline(self):
        before = app_fixture("web")
        current = deepcopy(before)
        desired = f"{SERVER}/lumero-web@{DIGESTS['web']}"
        release.container(current)["image"] = desired
        revision = revision_fixture(current)
        revision["properties"]["active"] = False
        with patch("release.time.monotonic", side_effect=[0, 0, 580, 590, 597, 600]), \
                patch("release.rest", side_effect=[current, revision]) as request, patch("release.time.sleep") as sleep, \
                self.assertRaises(common.Refusal):
            release.wait_ready(app_id("web"), before, desired, "web", SERVER)
        self.assertEqual(request.call_args_list, [call(app_id("web"), timeout=20),
                                                  call(app_id("web"), revision="web-fixture--old", timeout=10)])
        sleep.assert_called_once_with(3)

    def test_readiness_timeout_never_activates_or_rolls_back(self):
        before = app_fixture("web")
        with patch("release.time.monotonic", side_effect=[0, 2]), patch("release.rest") as request, self.assertRaises(common.Refusal):
            release.wait_ready(app_id("web"), before, "unused", "web", SERVER, seconds=1)
        request.assert_not_called()


if __name__ == "__main__":
    suite = unittest.defaultTestLoader.loadTestsFromModule(sys.modules[__name__])
    if suite.countTestCases() == 0:
        raise SystemExit("No delivery-helper cases were discovered.")
    result = unittest.TextTestRunner(verbosity=1).run(suite)
    raise SystemExit(0 if result.wasSuccessful() else 1)
