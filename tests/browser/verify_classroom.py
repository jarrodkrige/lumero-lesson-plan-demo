"""Verify the explicitly labelled loopback harness, never a deployed application."""

import argparse
import hashlib
import importlib.metadata
import ipaddress
import json
import re
import socket
import subprocess
import sys
import traceback
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlsplit

import cv2
from playwright.sync_api import expect, sync_playwright


ROOT = Path(__file__).resolve().parents[2]
NOTICE = "LOCAL VERIFICATION ONLY. Model outputs and presenter identity are synthetic."
PRIVATE_EXPLANATION = "The private marking explanation identifies the two neighbouring values being compared."


def loopback_origin(value):
    parsed = urlsplit(value)
    if (
        value != value.strip()
        or parsed.scheme != "http"
        or parsed.hostname not in {"localhost", "127.0.0.1", "::1"}
        or parsed.port is None
        or not 1024 <= parsed.port <= 65535
        or parsed.username is not None
        or parsed.password is not None
        or parsed.path not in {"", "/"}
        or parsed.query
        or parsed.fragment
    ):
        raise argparse.ArgumentTypeError("Use an explicit loopback HTTP origin with a port.")
    addresses = socket.getaddrinfo(parsed.hostname, parsed.port, type=socket.SOCK_STREAM)
    if not addresses or any(not ipaddress.ip_address(item[4][0]).is_loopback for item in addresses):
        raise argparse.ArgumentTypeError("The supplied hostname must resolve only to loopback addresses.")
    return value.rstrip("/")


class Verification:
    def __init__(self, base, output):
        self.base = base
        self.output = output
        self.output.mkdir(parents=True, exist_ok=True)
        self.phase = "starting"
        self.assets = []
        self.external_requests = []
        self.page_errors = []
        self.resource_failures = []
        self.http_errors = []
        self.report = {
            "scope": "Synthetic local verification only. Real local MCP, JWT validation, Tables, DOCX, QR and grading.",
            "not_verified": ["Live Entra sign-in", "Foundry inference", "Service-managed agent registration", "Azure deployment", "External phone reachability", "Comprehensive accessibility audit"],
            "started_utc": datetime.now(timezone.utc).isoformat(),
            "status": "running",
            "checks": [],
            "versions": {name: importlib.metadata.version(name) for name in ["playwright", "opencv-python-headless", "numpy", "greenlet", "pyee", "typing_extensions"]},
            "python": sys.version.split()[0],
        }
        revision = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True, check=True)
        changes = subprocess.run(["git", "status", "--porcelain", "--untracked-files=no"], cwd=ROOT, capture_output=True, text=True, check=True)
        self.report["source"] = {"commit": revision.stdout.strip(), "tracked_changes_present": bool(changes.stdout.strip())}

    def check(self, name, condition):
        self.phase = name
        if not condition:
            raise AssertionError(name)
        self.report["checks"].append({"name": name, "outcome": "passed"})

    def asset(self, name):
        if name not in self.assets:
            self.assets.append(name)
        return self.output / name

    def screenshot(self, page, name):
        page.screenshot(path=str(self.asset(name)), full_page=True, animations="disabled")

    def local_url(self, value):
        candidate = self.base + value if value.startswith("/") else value
        if urlsplit(candidate).netloc != urlsplit(self.base).netloc or urlsplit(candidate).scheme != "http":
            raise AssertionError("Only requests to the selected loopback origin are permitted.")
        return candidate

    def get(self, context, value):
        return context.request.get(self.local_url(value), max_redirects=0, timeout=15_000)

    def context(self, browser, **options):
        context = browser.new_context(service_workers="block", **options)
        context.set_default_timeout(15_000)

        def restrict(route):
            try:
                self.local_url(route.request.url)
            except AssertionError:
                self.external_requests.append("blocked external request")
                route.abort("blockedbyclient")
                return
            route.continue_()

        context.route("**/*", restrict)
        context.on("page", self.observe_page)
        return context

    @staticmethod
    def safe_route(url):
        return re.sub(r"[a-f0-9]{48}", "{id}", urlsplit(url).path)

    def observe_page(self, page):
        page.on("pageerror", lambda error: self.page_errors.append(type(error).__name__))

        def response_error(response):
            if response.status >= 400:
                self.http_errors.append({"status": response.status, "route": self.safe_route(response.url)})

        def failed(request):
            if request.url.endswith("/document") and request.failure == "net::ERR_ABORTED":
                return
            self.resource_failures.append({"type": request.resource_type, "route": self.safe_route(request.url)})

        page.on("response", response_error)
        page.on("requestfailed", failed)

    def visit(self, page, value):
        response = page.goto(self.local_url(value), wait_until="networkidle")
        if response is None or response.status != 200:
            raise AssertionError("Expected a successful local page navigation.")
        return response

    @staticmethod
    def layout_fits(page):
        return page.evaluate("document.documentElement.scrollWidth <= window.innerWidth")

    @staticmethod
    def native_navigation(page, button, path):
        with page.expect_navigation(wait_until="networkidle"):
            button.click()
        expect(page).to_have_url(re.compile(path))

    def run(self):
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch(headless=True)
            self.report["chromium"] = browser.version
            try:
                presenter = self.context(browser, viewport={"width": 1440, "height": 1000}, accept_downloads=True)
                notice = self.get(presenter, "/_local/notice")
                self.check("Harness explicitly discloses synthetic identity and models", notice.status == 200 and NOTICE in notice.text())
                page = presenter.new_page()
                self.visit(page, "/")
                self.check("Home has a main landmark and one heading", page.get_by_role("main").count() == 1 and page.get_by_role("heading", level=1).count() == 1)
                page.keyboard.press("Tab")
                focused_text = page.locator(":focus").inner_text()
                self.check("Keyboard navigation starts with the skip link", "skip" in focused_text.lower())
                self.screenshot(page, "00-home-desktop.png")
                self.visit(page, "/_local/sign-in/presenter")
                self.check("Synthetic presenter reaches the protected workspace", urlsplit(page.url).path == "/presenter")
                self.visit(page, "/presenter?example=robots")
                self.check("Robot sports day example prefills the form", "robot" in page.get_by_label("Topic", exact=True).input_value().lower() and "sports" in page.get_by_label("Topic", exact=True).input_value().lower())
                self.screenshot(page, "07-robot-example-desktop.png")
                self.visit(page, "/presenter?example=sorting")
                self.check("Sorting example prefills the form", "sort" in page.get_by_label("Topic", exact=True).input_value().lower())
                request = json.loads((ROOT / "examples" / "lesson-inputs" / "ocr-j277-sorting.json").read_text(encoding="utf-8-sig"))
                page.get_by_label("Topic", exact=True).fill(request["Topic"])
                page.get_by_label("Educational level", exact=True).select_option(label=request["EducationalLevel"])
                page.get_by_label("Duration in minutes", exact=True).fill(str(request["DurationMinutes"]))
                page.get_by_label("Teaching instructions (optional)", exact=True).fill(request["Instructions"])
                self.check("All lesson inputs have explicit accessible labels", all(page.get_by_label(label, exact=True).count() == 1 for label in ["Topic", "Educational level", "Duration in minutes", "Teaching instructions (optional)"]))
                font = page.locator("body").evaluate("element => getComputedStyle(element).fontFamily")
                self.check("Classroom stylesheet is applied", "Segoe UI" in font and self.layout_fits(page))
                self.screenshot(page, "01-presenter-desktop.png")
                page.set_viewport_size({"width": 390, "height": 844})
                self.check("Presenter form fits a mobile viewport", self.layout_fits(page))
                self.screenshot(page, "08-presenter-mobile.png")
                page.set_viewport_size({"width": 1440, "height": 1000})

                self.phase = "native lesson generation"
                self.native_navigation(page, page.get_by_role("button", name="Create lesson plan", exact=True), r"/presenter/lessons/[a-f0-9]{48}$")
                self.check("Generated lesson is explicitly local verification", "Local verification:" in page.get_by_role("heading", level=1).inner_text())
                self.check("Lesson duration has accessible word separation", "60 minutes" in page.locator("main").inner_text())
                self.check("Lesson exposes citation provenance", "source" in page.locator("main").inner_text().lower())
                self.screenshot(page, "02-lesson-desktop.png")
                self.phase = "lesson document download"
                with page.expect_download() as download_event:
                    page.get_by_role("link", name="Download lesson document", exact=True).click()
                document = self.asset("lesson-local-verification.docx")
                download_event.value.save_as(str(document))
                with zipfile.ZipFile(document) as package:
                    document_xml = package.read("word/document.xml").decode("utf-8")
                    self.check("Download is a real Open XML lesson document", "[Content_Types].xml" in package.namelist() and "Local verification:" in document_xml)
                self.phase = "native reviewed quiz drafting"
                self.native_navigation(page, page.get_by_role("button", name="Create five-question quiz", exact=True), r"/presenter/drafts/[a-f0-9]{48}$")
                draft_path = urlsplit(page.url).path
                quiz_id = draft_path.rsplit("/", 1)[1]
                quiz_url = self.base + "/quiz/" + quiz_id
                self.check("Private draft has exactly five reviewed questions", page.get_by_text("Correct answer", exact=True).count() == 5)
                self.check("Private draft keeps explanations for presenter review", page.get_by_text(PRIVATE_EXPLANATION, exact=True).count() == 5)
                self.screenshot(page, "03-private-draft-desktop.png")
                anonymous = self.context(browser, java_script_enabled=False, viewport={"width": 390, "height": 844})
                unpublished = self.get(anonymous, quiz_url)
                self.check("Anonymous quiz is unavailable before publication", unpublished.status == 404 and PRIVATE_EXPLANATION not in unpublished.text())
                approval = page.get_by_role("checkbox")
                self.check("Presenter approval is required and initially unselected", approval.count() == 1 and not approval.is_checked() and approval.get_attribute("required") is not None)
                page.get_by_role("button", name="Approve and publish quiz", exact=True).click()
                self.check("Native form blocks publication without approval", urlsplit(page.url).path == draft_path and not approval.is_checked() and self.get(anonymous, quiz_url).status == 404)
                approval.check()
                self.phase = "native MCP-backed publication"
                self.native_navigation(page, page.get_by_role("button", name="Approve and publish quiz", exact=True), re.escape(draft_path) + r"$")
                expect(page.get_by_text("Publication confirmed", exact=True)).to_be_visible()
                self.check("Committed publication exposes the canonical quiz link", page.locator(f'a[href="{quiz_url}"]').count() >= 1)
                self.check("Published draft no longer offers another approval", page.get_by_role("button", name="Approve and publish quiz", exact=True).count() == 0)
                qr = page.get_by_role("img", name="QR code to open the published pupil quiz. The same link is shown above.", exact=True)
                expect(qr).to_be_visible()
                qr_response = self.get(presenter, qr.get_attribute("src"))
                self.check("Verified canonical link exposes a PNG QR", qr_response.status == 200 and qr_response.headers.get("content-type") == "image/png")
                qr_path = self.asset("quiz-local-verification.png")
                qr_path.write_bytes(qr_response.body())
                decoded, _, _ = cv2.QRCodeDetectorAruco().detectAndDecode(cv2.imread(str(qr_path)))
                self.check("Locally rendered QR decodes to the exact canonical URL", decoded == quiz_url)
                self.screenshot(page, "04-published-desktop.png")
                self.visit(page, draft_path)
                self.check("Refreshing the draft preserves its published outcome", page.get_by_text("Publication confirmed", exact=True).count() == 1 and page.locator(f'a[href="{quiz_url}"]').count() >= 1)

                self.phase = "anonymous JavaScript-disabled participation"
                pupil = anonymous.new_page()
                public_response = self.visit(pupil, quiz_url)
                headers = public_response.headers
                self.check("Canonical public page has its exact publication marker", headers.get("x-lumero-quiz-id") == quiz_id)
                self.check("Public response has restrictive security headers", "no-store" in headers.get("cache-control", "") and headers.get("referrer-policy") == "no-referrer" and headers.get("x-content-type-options") == "nosniff" and "script-src 'none'" in headers.get("content-security-policy", ""))
                self.check("Anonymous page contains no scripts", pupil.locator("script").count() == 0)
                self.check("Anonymous quiz has five grouped questions and twenty options", pupil.locator("fieldset").count() == 5 and pupil.get_by_role("radio").count() == 20)
                self.check("Question legends and option labels have word separation", all(re.match(r"Question [1-5] of 5\s+\S", legend) for legend in pupil.locator("legend").all_inner_texts()) and all(re.match(r"[a-d]\s+\S", label.strip()) for label in pupil.locator("fieldset label").all_inner_texts()) and pupil.get_by_role("radio", name="Two neighbouring values", exact=True).count() == 5)
                public_html = pupil.content()
                self.check("Pre-submission document contains no marking keys or explanations", PRIVATE_EXPLANATION not in public_html and all(value not in public_html.lower() for value in ["correctoptionid", "correct answer", "marking explanation", "is_correct", "iscorrect"]))
                input_names = pupil.locator("input, textarea, select").evaluate_all("elements => elements.map(element => element.name)")
                self.check("Anonymous form asks for no participant identity", set(input_names) == {"__RequestVerificationToken", "attempt", "ticket", "answer-q1", "answer-q2", "answer-q3", "answer-q4", "answer-q5"})
                self.check("Anonymous quiz fits a mobile viewport", self.layout_fits(pupil))
                self.screenshot(pupil, "05-anonymous-quiz-mobile.png")
                pupil.get_by_role("button", name="Submit answers", exact=True).click()
                self.check("Native participant form requires every answer", pupil.url == quiz_url and pupil.locator("form").evaluate("form => !form.checkValidity()"))
                for number in range(1, 6):
                    choice = "a" if number <= 2 else "b"
                    pupil.locator(f'input[name="answer-q{number}"][value="{choice}"]').check()
                self.phase = "native server-graded submission"
                self.native_navigation(pupil, pupil.get_by_role("button", name="Submit answers", exact=True), r"/quiz/[a-f0-9]{48}/results/[a-f0-9]{48}$")
                expect(pupil.get_by_role("heading", name="Your quiz results", exact=True)).to_be_visible()
                result_url = pupil.url
                result_text = pupil.locator("main").inner_text()
                self.check("Server grading produces the expected two out of five", re.search(r"\b2\s+out of\s+5\b", result_text) is not None)
                self.check("Marking explanation is available only after submission", result_text.count(PRIVATE_EXPLANATION) == 5)
                self.check("Results fit a mobile viewport", self.layout_fits(pupil))
                self.screenshot(pupil, "06-results-mobile.png")
                self.visit(pupil, result_url)
                self.check("Result refresh preserves the persisted attempt and score", pupil.url == result_url and pupil.locator("main").inner_text() == result_text)
                self.check("Private result link carries no participant PII or query token", not urlsplit(result_url).query and "anyone" in result_text.lower() and "link" in result_text.lower())

                self.phase = "safe generated-text rendering"
                self.visit(page, "/presenter")
                hostile_text = "Sorting <script>window.__lumero_test=true</script>"
                page.get_by_label("Topic", exact=True).fill(hostile_text)
                page.get_by_label("Educational level", exact=True).select_option(label="GCSE (OCR J277)")
                page.get_by_label("Duration in minutes", exact=True).fill("60")
                self.native_navigation(page, page.get_by_role("button", name="Create lesson plan", exact=True), r"/presenter/lessons/[a-f0-9]{48}$")
                self.check("Generated markup is rendered as text without executable nodes", hostile_text in page.get_by_role("heading", level=1).inner_text() and page.locator("script").count() == 0 and page.evaluate("typeof window.__lumero_test === 'undefined'"))
                self.check("Browser made no external requests", not self.external_requests)
                self.check("Rendered flow has no page errors or failed resources", not self.page_errors and not self.resource_failures and not self.http_errors)
                presenter.close()
                anonymous.close()
            finally:
                browser.close()

    def save(self):
        self.report["completed_utc"] = datetime.now(timezone.utc).isoformat()
        self.report["passed_checks"] = len(self.report["checks"])
        self.report["browser_diagnostics"] = {"page_errors": self.page_errors, "failed_resources": self.resource_failures, "http_errors": self.http_errors, "blocked_external_requests": len(self.external_requests)}
        self.report["artefacts"] = [
            {"file": name, "sha256": hashlib.sha256((self.output / name).read_bytes()).hexdigest(), "bytes": (self.output / name).stat().st_size}
            for name in self.assets if (self.output / name).is_file()
        ]
        (self.output / "browser-verification.json").write_text(json.dumps(self.report, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", type=loopback_origin, required=True)
    parser.add_argument("--output", type=Path, required=True, help="Directory for explicitly synthetic local evidence; do not commit runtime data.")
    args = parser.parse_args()
    verification = Verification(args.url, args.output.resolve())
    try:
        verification.run()
        verification.report["status"] = "passed"
        print(f"PASS: {len(verification.report['checks'])} synthetic local browser checks.")
        return 0
    except Exception as error:
        frames = traceback.extract_tb(error.__traceback__)
        verification.report["status"] = "failed"
        verification.report["failure"] = {"phase": verification.phase, "type": type(error).__name__, "script_line": frames[-1].lineno if frames else None}
        print(f"FAIL: {verification.phase} ({type(error).__name__}). No cookies or form tokens were recorded.", file=sys.stderr)
        return 1
    finally:
        verification.save()


if __name__ == "__main__":
    raise SystemExit(main())
