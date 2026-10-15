#!/usr/bin/env python3
"""Run the published Web image with synthetic media and disposable bind mounts.

Requires Docker and the pinned Playwright client/browser. No registry writes,
production media, host configuration, or credentials are used.
"""

import argparse
import hashlib
import json
from pathlib import Path
import platform
import shutil
import subprocess
import tempfile
import time
import uuid
from urllib.error import URLError
from urllib.request import urlopen

from playwright.sync_api import expect, sync_playwright


def docker(*args, check=True):
    result = subprocess.run(["docker", *args], capture_output=True, text=True)
    if check and result.returncode:
        # Container logs and environment may contain the generated login password.
        # Report command identity only; never dump docker inspect/logs on failure.
        raise RuntimeError(f"docker {args[0]} failed (exit {result.returncode})")
    return result.stdout.strip()


def wait_for(predicate, description, timeout=90):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.25)
    raise AssertionError(f"Timed out waiting for {description}")


def media_manifest(folder):
    return {str(p.relative_to(folder)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in folder.rglob("*") if p.is_file()}


def wait_for_http(base):
    def ready():
        try:
            with urlopen(base + "/login", timeout=2) as response:
                return response.status == 200
        except (URLError, TimeoutError, ConnectionError):
            return False
    wait_for(ready, "Web listener on container port 8080")


def login(context, base, password):
    gate = context.request.get(base + "/", max_redirects=0)
    assert gate.status == 302 and gate.headers["location"].startswith("/login")
    bad = context.request.post(base + "/auth/login", form={"password": "invalid"}, max_redirects=0)
    assert bad.status == 302 and bad.headers["location"].startswith("/login?error=1")
    response = context.request.post(base + "/auth/login", form={"password": password}, max_redirects=0)
    assert response.status == 302 and response.headers["location"] == "/"
    assert "httponly" in response.headers["set-cookie"].lower()
    assert context.request.get(base + "/").status == 200
    assert context.request.get(base + "/_framework/blazor.server.js").status == 200


def scan_and_thumbnail(page, context, base):
    page.goto(base, wait_until="networkidle")
    reset = page.get_by_role("button", name="Reset", exact=True)
    if reset.count():
        reset.click()
        expect(page.get_by_text("Scan complete", exact=False)).not_to_be_visible()
    start = page.get_by_role("button", name="Start Scan", exact=True)
    expect(start).to_be_enabled(timeout=60_000)
    start.click()
    expect(page.get_by_text("Scan complete", exact=False)).to_be_visible(timeout=180_000)
    restored_results_and_thumbnails(page, context, base)


def restored_results_and_thumbnails(page, context, base, expected_files=2):
    page.goto(base + "/results")
    expect(page.locator(".result-count")).to_contain_text(f"{expected_files} file(s)")
    for kind in ("hq", "full"):
        response = context.request.get(base + f"/thumbnail/{kind}", params={"path": "/media/sample-a.mp4", "t": "1"})
        assert response.status == 200, f"{kind} thumbnail returned {response.status}"
        assert response.headers["content-type"].startswith("image/jpeg")
        payload = response.body()
        assert payload.startswith(b"\xff\xd8") and len(payload) > 500


def read_json(path):
    try:
        return json.loads(path.read_text())
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def completed_schedule(state):
    saved = read_json(state / "WebSchedule.json")
    return bool(saved.get("lastClaimedSlotUtc") and saved.get("lastStartedUtc") and saved.get("lastCompletedUtc"))


def assert_favourite_plan(state, expected_files=2):
    plan = read_json(state / "SelectionPlan.json")
    assert plan["version"] == 1
    decisions = plan["plan"]["decisions"]
    assert len(decisions) == expected_files
    favourite = next(row for row in decisions if row["path"] == "/media/sample-a.mp4")
    assert favourite["isProtected"] and not favourite["removeCandidate"], "NFO favourite was not protected"


def run(image, arch, revision=None, source_url=None):
    native = {"x86_64": "amd64", "amd64": "amd64", "aarch64": "arm64", "arm64": "arm64"}.get(platform.machine().lower())
    assert native == arch, f"Smoke must run natively: host={native}, requested={arch}"
    assert docker("image", "inspect", image, "--format", "{{.Architecture}}") == arch
    labels = json.loads(docker("image", "inspect", image, "--format", "{{json .Config.Labels}}"))
    assert labels.get("org.opencontainers.image.source") and labels.get("org.opencontainers.image.revision")
    if revision is not None:
        assert labels["org.opencontainers.image.revision"] == revision, "Image source revision does not match checkout"
    if source_url is not None:
        assert labels["org.opencontainers.image.source"] == source_url, "Image source URL does not match checkout"
    expected_uname = {"amd64": "x86_64", "arm64": "aarch64"}[arch]
    assert docker("run", "--rm", "--entrypoint", "uname", image, "-m") == expected_uname
    container = "vdf-web-smoke-" + uuid.uuid4().hex
    with tempfile.TemporaryDirectory(prefix="vdf-web-smoke-") as temporary:
        root = Path(temporary)
        media, config, state = (root / name for name in ("media", "config", "state"))
        for folder in (media, config, state):
            folder.mkdir()
        # Generate with the image's own architecture-specific ffmpeg, then mount
        # the resulting media read-only in the actual Web process.
        docker("run", "--rm", "--mount", f"type=bind,src={media},dst=/fixtures",
               "--entrypoint", "ffmpeg", image, "-hide_banner", "-loglevel", "error",
               "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=12", "-t", "4",
               "-c:v", "mpeg4", "-q:v", "3", "/fixtures/sample-a.mp4")
        shutil.copyfile(media / "sample-a.mp4", media / "sample-b.mp4")
        (media / "sample-a.nfo").write_text("<movie><favorite>true</favorite></movie>\n")
        before = media_manifest(media)

        def start_container():
            docker("run", "--detach", "--name", container, "--publish", "127.0.0.1::8080",
                   "--mount", f"type=bind,src={media},dst=/media,readonly",
                   "--mount", f"type=bind,src={config},dst=/root/.config/VDF",
                   "--mount", f"type=bind,src={state},dst=/root/.local/state/VDF", image)
            port = docker("port", container, "8080/tcp").rsplit(":", 1)[1]
            return "http://127.0.0.1:" + port

        def stop_container():
            docker("stop", "--time", "20", container, check=False)
            docker("rm", "--force", container, check=False)

        try:
            base = start_container()
            wait_for_http(base)
            credentials = config / "web-credentials.json"
            wait_for(lambda: credentials.is_file(), "generated credentials")
            password = json.loads(credentials.read_text())["password"]
            assert password
            with sync_playwright() as playwright:
                browser = playwright.chromium.launch()
                context = browser.new_context()
                login(context, base, password)
                page = context.new_page()
                page.goto(base, wait_until="networkidle")
                page.get_by_placeholder("/path/to/scan", exact=True).fill("/media")
                page.locator(".add-path").first.get_by_role("button", name="Add", exact=True).click()
                page.get_by_role("button", name="Save settings", exact=True).click()
                expect(page.get_by_text("Settings saved.", exact=True)).to_be_visible()
                settings_file = config / "web-settings.json"
                settings = json.loads(settings_file.read_text())
                assert settings["IncludeList"] == ["/media"]
                assert settings["UseNativeFfmpegBinding"] is False
                page.goto(base + "/automation", wait_until="networkidle")
                page.get_by_label("Enable local NFO protection", exact=True).check()
                page.get_by_label("Automatically prepare selection after scans", exact=True).check()
                page.get_by_role("button", name="Save automation settings", exact=True).click()
                expect(page.get_by_text("Automation settings saved.", exact=True)).to_be_visible()
                scan_and_thumbnail(page, context, base)
                database = state / "ScannedFiles.db"
                wait_for(lambda: database.is_file() and database.stat().st_size > 16, "persisted scan database")
                settings_bytes = settings_file.read_bytes()
                assert_favourite_plan(state)
                browser.close()

                # Recreate the container so data in its writable layer cannot
                # accidentally satisfy the persistence assertions.
                stop_container()
                assert media_manifest(media) == before, "Read-only source media changed"
                # A newly introduced duplicate proves the browser-free run did
                # real scan work rather than merely retaining the first results.
                shutil.copyfile(media / "sample-a.mp4", media / "sample-c.mp4")
                before = media_manifest(media)
                automation_file = config / "web-settings.automation.json"
                automation = read_json(automation_file)
                # Use a long interval with an immediately due catch-up slot. Keep
                # the next slot well beyond this check even near a UTC boundary.
                interval = 10080
                if interval * 60 - time.time() % (interval * 60) < 600:
                    interval -= 1
                automation["schedule"].update(enabled=True, kind="Interval", intervalMinutes=interval, catchUpHours=168)
                replacement = config / ".automation-smoke.tmp"
                replacement.write_text(json.dumps(automation))
                replacement.replace(automation_file)
                base = start_container()
                wait_for_http(base)
                assert json.loads(credentials.read_text())["password"] == password
                assert settings_file.read_bytes() == settings_bytes
                # There is no browser or Blazor circuit during this scan. Only
                # the server's hosted scheduler can produce this completion.
                wait_for(lambda: completed_schedule(state), "scan scheduled without a browser", timeout=180)
                scheduled = read_json(state / "WebSchedule.json")
                assert scheduled["status"] == "Completed", "Scheduled scan did not complete successfully"
                assert scheduled["durationSeconds"] >= 0
                assert_favourite_plan(state, expected_files=3)
                saved_results = read_json(state / "WebResults.json")
                assert saved_results["version"] == 1 and len(saved_results["rows"]) == 3
                assert any(row["item"]["path"] == "/media/sample-c.mp4" for row in saved_results["rows"])
                assert not list(state.glob("*_DAMAGED.db")), "Persisted database was rejected"
                # Recreate a second time. The already claimed slot must remain
                # claimed and the prior result/selection must be restored.
                stop_container()
                base = start_container()
                wait_for_http(base)
                # Observe longer than one 15-second scheduling tick, with no
                # browser and no manually invoked schedule endpoint.
                deadline = time.monotonic() + 20
                while time.monotonic() < deadline:
                    current = read_json(state / "WebSchedule.json")
                    assert current.get("lastClaimedSlotUtc") == scheduled["lastClaimedSlotUtc"]
                    assert current.get("lastStartedUtc") == scheduled["lastStartedUtc"]
                    assert current.get("lastCompletedUtc") == scheduled["lastCompletedUtc"]
                    time.sleep(0.5)
                assert read_json(state / "WebResults.json") == saved_results
                assert_favourite_plan(state, expected_files=3)
                browser = playwright.chromium.launch()
                context = browser.new_context()
                login(context, base, password)
                page = context.new_page()
                page.goto(base)
                expect(page.locator(".path-entry").filter(has_text="/media")).to_be_visible()
                restored_results_and_thumbnails(page, context, base, expected_files=3)
                browser.close()
            assert media_manifest(media) == before, "Read-only source media changed"
            print(f"Web container smoke passed natively on {arch}: login, scan, JPEG, NFO, persistence, browser-free schedule, read-only media")
        finally:
            stop_container()
            # Docker creates files as root, including on non-root CI hosts.
            docker("run", "--rm", "--mount", f"type=bind,src={root},dst=/smoke",
                   "--entrypoint", "chmod", image, "-R", "a+rwX", "/smoke", check=False)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", default="vdf-web:validation")
    parser.add_argument("--arch", required=True, choices=("amd64", "arm64"))
    parser.add_argument("--revision", help="Expected OCI source revision (required by CI)")
    parser.add_argument("--source-url", help="Expected OCI source repository URL (required by CI)")
    args = parser.parse_args()
    run(args.image, args.arch, args.revision, args.source_url)
