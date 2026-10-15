"""Exercise the real workflow helper and final release/image commands offline."""

import gzip
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

import yaml

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / ".github/scripts/publication.py"
PREFIX = ET.parse(ROOT / "Directory.Build.props").findtext(".//VersionPrefix")
VERSION = PREFIX.rsplit(".", 1)[0] + ".123"
TAG = "v" + VERSION


def workflow(name):
    # BaseLoader keeps the Actions key 'on' and boolean expressions as strings.
    return yaml.load((ROOT / f".github/workflows/{name}.yml").read_text(), Loader=yaml.BaseLoader)


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "Directory.Build.props").write_bytes((ROOT / "Directory.Build.props").read_bytes())
        helper = self.root / ".github/scripts/publication.py"
        helper.parent.mkdir(parents=True)
        helper.write_bytes(SCRIPT.read_bytes())
        self.git("init", "-q")
        self.git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "--allow-empty", "-m", "fixture")
        self.sha = self.git("rev-parse", "HEAD").strip()
        self.git("tag", TAG)
        self.git("remote", "add", "origin", str(self.root))
        self.output = self.root / "output"
        self.log = self.root / "commands.jsonl"
        self.bin = self.root / "bin"
        self.bin.mkdir()
        gh = self.bin / "gh"
        gh.write_text("#!/usr/bin/env python3\n" + (ROOT / ".github/tests/recording_gh.py").read_text())
        gh.chmod(0o755)
        stub = self.bin / "docker"
        stub.write_text("#!/usr/bin/env python3\nimport json,os,sys\n"
                            "with open(os.environ['COMMAND_LOG'],'a') as f: f.write(json.dumps(sys.argv[1:])+'\\n')\n"
                            "if sys.argv[1:] == ['load']:\n"
                            " with open(os.environ['COMMAND_LOG']+'.arch','w') as f: f.write(sys.stdin.buffer.read().decode().split(':')[-1])\n"
                            "if sys.argv[1:3] == ['image','inspect']:\n"
                            " with open(os.environ['COMMAND_LOG']+'.arch') as f: print(f.read())\n"
                            "if sys.argv[1:2] == ['push'] and sys.argv[-1].endswith(os.environ.get('CHANGE_TAG_AFTER_PUSH','no-change')):\n"
                            " import subprocess\n"
                            " args=['git','-C',os.environ['FIXTURE_ORIGIN'],'update-ref']\n"
                            " if os.environ.get('DELETE_FIXTURE_TAG'): args+=['-d','refs/tags/'+os.environ['FIXTURE_TAG']]\n"
                            " else: args+=['refs/tags/'+os.environ['FIXTURE_TAG'],os.environ['FIXTURE_NEXT_SHA']]\n"
                            " subprocess.run(args,check=True)\n"
                            )
        stub.chmod(0o755)
        self.env = dict(os.environ, GITHUB_EVENT_NAME="push", GITHUB_REF_TYPE="branch",
                        GITHUB_REF_NAME="master", GITHUB_SHA=self.sha,
                        GITHUB_REPOSITORY="BenCodez/videoduplicatefinder",
                        GITHUB_REPOSITORY_OWNER="BenCodez", REQUEST_PUBLISH="false",
                        EXPECTED_SOURCE_SHA="",
                        GITHUB_OUTPUT=str(self.output), COMMAND_LOG=str(self.log),
                        PATH=str(self.bin) + os.pathsep + os.environ["PATH"])

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, text=True)

    def run_helper(self, command="prepare", allow_release=False, **changes):
        self.output.unlink(missing_ok=True)
        args = [sys.executable, str(SCRIPT), command]
        if allow_release:
            args.append("--allow-release")
        result = subprocess.run(args, cwd=self.root,
                                env=dict(self.env, **changes), capture_output=True, text=True)
        values = dict(line.split("=", 1) for line in self.output.read_text().splitlines()) if self.output.exists() else {}
        return result, values

    def publication(self):
        return dict(GITHUB_EVENT_NAME="workflow_dispatch", GITHUB_REF_TYPE="tag",
                    GITHUB_REF_NAME=TAG, REQUEST_PUBLISH="true")

    def release_publication(self, **release_changes):
        payload = self.root / "release-event.json"
        payload.write_text(json.dumps({"action": "published", "release": {
            "tag_name": TAG, "draft": False, "prerelease": False, **release_changes}}))
        return dict(GITHUB_EVENT_NAME="release", GITHUB_EVENT_PATH=str(payload),
                    GITHUB_REF_TYPE="tag", GITHUB_REF_NAME=TAG, REQUEST_PUBLISH="false")

    def commands(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def bodies(self):
        return [json.loads(line) for line in Path(str(self.log) + ".bodies").read_text().splitlines()]

    def independent_origin(self):
        self.git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "--allow-empty", "-m", "next")
        next_sha = self.git("rev-parse", "HEAD").strip()
        origin = self.root / "upstream.git"
        subprocess.run(["git", "clone", "-q", "--bare", str(self.root), str(origin)], check=True)
        self.git("checkout", "-q", "--detach", self.sha)
        self.git("remote", "set-url", "origin", str(origin))
        return origin, next_sha

    def packages(self):
        assets = self.root / "release-assets"
        assets.mkdir()
        for target in ("linux-x64", "linux-arm64", "win-x64", "win-arm64", "osx-x64", "osx-arm64"):
            ext = "zip" if target.startswith("win-") else "tar.gz"
            for app in ("GUI", "CLI", "Web"):
                (assets / f"{app}-{target}.{ext}").write_bytes(b"synthetic package")
        return assets

    def test_push_pr_tag_and_default_manual_run_never_publish(self):
        for event in ("push", "pull_request", "workflow_dispatch"):
            for ref_type, ref_name in (("branch", "master"), ("tag", TAG)):
                with self.subTest(event=event, ref=ref_type):
                    result, values = self.run_helper(allow_release=True, GITHUB_EVENT_NAME=event, GITHUB_REF_TYPE=ref_type, GITHUB_REF_NAME=ref_name)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(values["publish"], "false")
                    self.assertEqual(values["image"], "ghcr.io/bencodez/vdf-web")
                    self.assertEqual(values["version"], VERSION if ref_type == "tag" else PREFIX)
                    result, _ = self.run_helper("publish-native", GITHUB_EVENT_NAME=event, GITHUB_REF_TYPE=ref_type, GITHUB_REF_NAME=ref_name)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertEqual(self.commands(), [])

    def test_manual_versioned_run_matches_exact_source(self):
        result, values = self.run_helper(**self.publication())
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(values["publish"], "true")
        self.assertEqual(values["tag"], TAG)
        self.assertEqual(values["sha"], self.sha)

    def test_expected_source_sha_accepts_exact_match_and_rejects_invalid_or_different_commit(self):
        result, values = self.run_helper(**self.publication(), EXPECTED_SOURCE_SHA=self.sha)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(values["sha"], self.sha)
        for expected in ("0" * 40, self.sha[:7], self.sha + "\n", "invalid"):
            with self.subTest(expected=expected):
                result, values = self.run_helper(**self.publication(), EXPECTED_SOURCE_SHA=expected)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(values, {})
        self.assertEqual(self.commands(), [])

    def test_dispatch_pin_blocks_tag_moved_before_docker_run_resolves_it(self):
        self.packages()
        result = self.run_native_publication_steps()
        self.assertEqual(result.returncode, 0, result.stderr)
        dispatch = self.commands()[-1]
        expected = next(arg.split("=", 1)[1] for arg in dispatch if arg.startswith("expected_sha="))
        self.assertEqual(expected, self.sha)
        self.log.unlink()
        # Simulate tag resolution after dispatch: checkout, GITHUB_SHA and live
        # tag all agree on the new commit, but the native release used the old one.
        self.git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "--allow-empty", "-m", "moved")
        moved = self.git("rev-parse", "HEAD").strip()
        self.git("tag", "-f", TAG)
        event = self.publication() | {"GITHUB_SHA": moved, "EXPECTED_SOURCE_SHA": expected}
        result, values = self.run_helper(allow_release=True, **event)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(values, {})
        self.web_artifacts()
        step = workflow("docker")["jobs"]["publish"]["steps"][-1]
        result = subprocess.run(["bash", "-c", step["run"]], cwd=self.root, capture_output=True,
                                env=self.env | event | {"IMAGE": "ghcr.io/bencodez/vdf-web", "VERSION": VERSION, "SOURCE_SHA": moved})
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any(row[0] == "push" or row[:2] == ["buildx", "imagetools"] for row in self.commands()))

    def test_published_release_requires_docker_opt_in_and_matches_exact_source(self):
        for prerelease in (False, True):
            event = self.release_publication(prerelease=prerelease)
            result, values = self.run_helper(**event)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(values["publish"], "false")
            result, values = self.run_helper(allow_release=True, **event)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(values["publish"], "true")
            self.assertEqual(values["sha"], self.sha)
            self.assertEqual(values["version"], VERSION)
            result, _ = self.run_helper("publish-native", **event)
            self.assertNotEqual(result.returncode, 0)
            result, _ = self.run_helper("publish-native", allow_release=True, **event)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(self.commands(), [])

    def test_draft_mismatched_or_nonversion_release_cannot_publish(self):
        for release in ({"draft": True}, {"draft": None}, {"tag_name": "different"}):
            result, values = self.run_helper(allow_release=True, **self.release_publication(**release))
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(values, {})
        for changes in ({"GITHUB_REF_TYPE": "branch"}, {"GITHUB_REF_NAME": "latest"},
                        {"GITHUB_REPOSITORY": "example/fork"}, {"GITHUB_SHA": "0" * 40}):
            result, values = self.run_helper(allow_release=True, **(self.release_publication() | changes))
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(values, {})
        self.assertEqual(self.commands(), [])

    def test_other_release_events_and_malformed_payload_do_not_publish(self):
        event = self.release_publication()
        payload = Path(event["GITHUB_EVENT_PATH"])
        for action in ("created", "edited", "deleted", "unpublished"):
            payload.write_text(json.dumps({"action": action}))
            result, values = self.run_helper(allow_release=True, **event)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(values["publish"], "false")
        for content in ("invalid", "[]", '{"action":"published"}', '{"action":"published","release":null}'):
            payload.write_text(content)
            result, values = self.run_helper(allow_release=True, **event)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(values, {})
        self.assertEqual(self.commands(), [])

    def test_annotated_tag_is_accepted(self):
        self.git("tag", "-d", TAG)
        self.git("-c", "user.name=Test", "-c", "user.email=test@example.com", "tag", "-a", TAG, "-m", "fixture")
        result, values = self.run_helper(**self.publication())
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(values["publish"], "true")

    def test_unsafe_neighbors_fail_without_partial_outputs_or_writes(self):
        cases = [{"GITHUB_REF_TYPE": "branch"}, {"GITHUB_EVENT_NAME": "push"},
                 {"GITHUB_EVENT_NAME": "pull_request"}, {"GITHUB_REPOSITORY": "example/fork"},
                 {"GITHUB_SHA": "0" * 40}, {"GITHUB_REPOSITORY_OWNER": "owner\npublish=true"}]
        cases += [{"REQUEST_PUBLISH": value} for value in ("1", "True", "true\n", "true\r\n", "true\u2028", "")]
        cases += [{"GITHUB_REF_NAME": value} for value in ("latest", "4.1.x", "v99.98.1", TAG + "\n",
                   TAG + "\r\n", TAG + "\u2028", "é" + TAG, TAG + ";echo unsafe", "../" + TAG, "v1.2", TAG + "-rc1")]
        for changes in cases:
            with self.subTest(changes=changes):
                result, values = self.run_helper(**(self.publication() | changes))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(values, {})
                result, _ = self.run_helper("publish-native", **(self.publication() | changes))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.commands(), [])

    def test_moved_tag_is_rejected_at_final_consumer(self):
        self.git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "--allow-empty", "-m", "next")
        self.env["GITHUB_SHA"] = self.git("rev-parse", "HEAD").strip()
        self.packages()
        result, _ = self.run_helper("publish-native", **self.publication())
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.commands(), [])

    def test_missing_tag_is_rejected(self):
        self.git("tag", "-d", TAG)
        result, values = self.run_helper(**self.publication())
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(values, {})

    def test_live_remote_tag_moved_or_deleted_with_stale_local_checkout_is_rejected(self):
        origin, next_sha = self.independent_origin()
        self.packages()
        for state in ("moved", "deleted"):
            with self.subTest(state=state):
                if state == "moved":
                    subprocess.run(["git", "-C", str(origin), "update-ref", "refs/tags/" + TAG, next_sha], check=True)
                else:
                    subprocess.run(["git", "-C", str(origin), "update-ref", "-d", "refs/tags/" + TAG], check=True)
                self.assertEqual(self.git("rev-parse", "refs/tags/" + TAG).strip(), self.sha)
                result, values = self.run_helper(**self.publication())
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(values, {})
                result, _ = self.run_helper("publish-native", **self.publication())
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.commands(), [])

    def test_unavailable_remote_blocks_publication(self):
        self.git("remote", "set-url", "origin", str(self.root / "missing.git"))
        result, values = self.run_helper(**self.publication())
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(values, {})

    def test_native_consumer_requires_all_eighteen_nonempty_packages(self):
        assets = self.packages()
        victim = assets / "GUI-linux-x64.tar.gz"
        for state in ("missing", "empty", "symlink"):
            with self.subTest(state=state):
                victim.unlink(missing_ok=True)
                if state == "empty":
                    victim.touch()
                elif state == "symlink":
                    victim.symlink_to(assets / "CLI-linux-x64.tar.gz")
                result, _ = self.run_helper("publish-native", **self.publication())
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.commands(), [])

    def test_native_publisher_passes_exact_assets_and_never_promotes_latest(self):
        assets = self.packages()
        result, _ = self.run_helper("publish-native", **self.publication())
        self.assertEqual(result.returncode, 0, result.stderr)
        commands = self.commands()
        self.assertEqual(len(commands), 21)  # List, create, eighteen uploads, publish by ID.
        self.assertIn("--paginate", commands[0])
        create = self.bodies()[1]
        self.assertEqual(create["tag_name"], TAG)
        self.assertEqual(create["target_commitish"], self.sha)
        self.assertEqual(create["draft"], True)
        self.assertEqual(create["make_latest"], "false")
        uploaded = commands[2:20]
        self.assertEqual({row[row.index("--input")+1] for row in uploaded}, {str(path.relative_to(self.root)) for path in assets.iterdir()})
        self.assertTrue(all("https://uploads.github.com/repos/BenCodez/videoduplicatefinder/releases/753/assets?name=" in row[3] for row in uploaded))
        self.assertEqual(commands[-1], ["api", "--method", "PATCH", "repos/BenCodez/videoduplicatefinder/releases/753", "--input", "-"])
        self.assertEqual(self.bodies()[-1], {"draft": False, "make_latest": "false"})

    def test_failed_creation_or_upload_never_publishes_or_deletes_draft(self):
        self.packages()
        for failure in ({"FAIL_CREATE": "1"}, {"FAIL_UPLOAD": "Web-osx-arm64.tar.gz"}):
            with self.subTest(failure=failure):
                self.log.unlink(missing_ok=True)
                result, _ = self.run_helper("publish-native", **self.publication(), **failure)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(any("PATCH" in command or "DELETE" in command for command in self.commands()))

    def test_existing_draft_or_published_release_on_later_page_blocks_all_writes(self):
        self.packages()
        for draft in (True, False):
            with self.subTest(draft=draft):
                self.log.unlink(missing_ok=True)
                pages = [[{"tag_name": "older", "draft": False}], [{"tag_name": TAG, "draft": draft}]]
                result, _ = self.run_helper("publish-native", **self.publication(), EXISTING_RELEASES=json.dumps(pages))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(len(self.commands()), 1)
                self.assertIn("--paginate", self.commands()[0])

    def test_unavailable_or_malformed_release_list_blocks_writes(self):
        self.packages()
        for changes in ({"FAIL_LIST": "1"}, {"EXISTING_RELEASES": "invalid"}, {"EXISTING_RELEASES": "{}"}, {"EXISTING_RELEASES": "[[{}]]"}):
            with self.subTest(changes=changes):
                self.log.unlink(missing_ok=True)
                result, _ = self.run_helper("publish-native", **self.publication(), **changes)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(len(self.commands()), 1)

    def test_tag_move_during_upload_leaves_new_draft_unpublished(self):
        origin, next_sha = self.independent_origin()
        self.packages()
        result, _ = self.run_helper("publish-native", **self.publication(), MOVE_TAG_AFTER_UPLOAD="Web-osx-arm64.tar.gz",
                                    FIXTURE_ORIGIN=str(origin), FIXTURE_TAG=TAG, FIXTURE_NEXT_SHA=next_sha)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(self.commands()), 20)
        self.assertFalse(any("PATCH" in command or "DELETE" in command for command in self.commands()))

    def run_native_publication_steps(self, **changes):
        # Run the actual workflow's publish/dispatch shell in order. Like Actions,
        # stop after a failed step; the external gh boundary alone is simulated.
        result = None
        for step in workflow("releases")["jobs"]["publish-release"]["steps"]:
            if "run" not in step:
                continue
            result = subprocess.run(["bash", "-e", "-c", step["run"]], cwd=self.root,
                                    capture_output=True, text=True,
                                    env=self.env | self.publication() | {"VERSION_TAG": TAG} | changes)
            if result.returncode:
                break
        return result

    def test_native_workflow_dispatches_exact_version_only_after_publishing(self):
        self.packages()
        result = self.run_native_publication_steps()
        self.assertEqual(result.returncode, 0, result.stderr)
        commands = self.commands()
        self.assertEqual(commands[-2], ["api", "--method", "PATCH",
                                        "repos/BenCodez/videoduplicatefinder/releases/753", "--input", "-"])
        self.assertEqual(commands[-1], ["workflow", "run", "docker.yml", "--repo",
                                        "BenCodez/videoduplicatefinder", "--ref", TAG, "-f", "publish=true",
                                        "-f", "expected_sha=" + self.sha])

    def test_failed_native_publication_never_dispatches_docker(self):
        self.packages()
        for failure in ({"FAIL_CREATE": "1"}, {"FAIL_UPLOAD": "Web-osx-arm64.tar.gz"}, {"FAIL_PUBLISH": "1"}):
            with self.subTest(failure=failure):
                self.log.unlink(missing_ok=True)
                result = self.run_native_publication_steps(**failure)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(any(row[:2] == ["workflow", "run"] for row in self.commands()))

    def test_tag_move_after_native_publication_blocks_dispatch(self):
        origin, next_sha = self.independent_origin()
        self.packages()
        result = self.run_native_publication_steps(MOVE_TAG_AFTER_PUBLISH="1", FIXTURE_ORIGIN=str(origin),
                                                   FIXTURE_TAG=TAG, FIXTURE_NEXT_SHA=next_sha)
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue(any("PATCH" in row for row in self.commands()))
        self.assertFalse(any(row[:2] == ["workflow", "run"] for row in self.commands()))

    def test_failed_docker_dispatch_is_reported_without_republishing_native_release(self):
        self.packages()
        result = self.run_native_publication_steps(FAIL_DISPATCH="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(sum(row[:2] == ["workflow", "run"] for row in self.commands()), 1)
        self.assertEqual(sum("PATCH" in row for row in self.commands()), 1)

    def web_artifacts(self):
        for arch in ("amd64", "arm64"):
            artifact = self.root / f"web-images/web-{arch}/web-image.tar.gz"
            artifact.parent.mkdir(parents=True, exist_ok=True)
            artifact.write_bytes(gzip.compress(f"synthetic image:{arch}".encode()))

    def run_web_consumer(self, publication=None, **changes):
        event_context = self.publication() if publication is None else publication
        result, values = self.run_helper(allow_release=True, **event_context)
        self.assertEqual(result.returncode, 0, result.stderr)
        step = workflow("docker")["jobs"]["publish"]["steps"][-1]
        return subprocess.run(["bash", "-c", step["run"]], cwd=self.root, capture_output=True,
                              env=dict(self.env | event_context | changes, IMAGE=values["image"], VERSION=values["version"], SOURCE_SHA=values["sha"]))

    def test_published_release_runs_real_web_consumer_for_both_architectures(self):
        self.web_artifacts()
        result = self.run_web_consumer(publication=self.release_publication())
        self.assertEqual(result.returncode, 0, result.stderr)
        commands = self.commands()
        self.assertEqual(sum(row[0] == "push" for row in commands), 2)
        self.assertEqual(commands[-1][0:3], ["buildx", "imagetools", "create"])
        self.assertIn(f"ghcr.io/bencodez/vdf-web:{VERSION}", commands[-1])
        self.assertFalse(any("latest" in arg for row in commands for arg in row))

    def test_release_tag_move_during_upload_blocks_manifest(self):
        origin, next_sha = self.independent_origin()
        self.web_artifacts()
        result = self.run_web_consumer(publication=self.release_publication(), CHANGE_TAG_AFTER_PUSH="arm64",
                                       FIXTURE_ORIGIN=str(origin), FIXTURE_TAG=TAG, FIXTURE_NEXT_SHA=next_sha)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any(row[:2] == ["buildx", "imagetools"] for row in self.commands()))

    def test_web_final_consumer_publishes_both_validated_images_with_lowercase_tags(self):
        self.web_artifacts()
        result = self.run_web_consumer()
        self.assertEqual(result.returncode, 0, result.stderr)
        commands = self.commands()
        for arch in ("amd64", "arm64"):
            self.assertIn(["push", f"ghcr.io/bencodez/vdf-web:{self.sha}-{arch}"], commands)
        self.assertEqual(commands[-1], ["buildx", "imagetools", "create", "--tag", f"ghcr.io/bencodez/vdf-web:{VERSION}",
                                        "--tag", f"ghcr.io/bencodez/vdf-web:{self.sha}",
                                        f"ghcr.io/bencodez/vdf-web:{self.sha}-amd64", f"ghcr.io/bencodez/vdf-web:{self.sha}-arm64"])

    def test_missing_corrupt_or_wrong_architecture_web_artifact_blocks_registry_writes(self):
        for state in ("missing", "corrupt", "wrong-architecture"):
            with self.subTest(state=state):
                self.log.unlink(missing_ok=True)
                self.web_artifacts()
                artifact = self.root / "web-images/web-arm64/web-image.tar.gz"
                if state == "missing":
                    artifact.unlink()
                elif state == "corrupt":
                    artifact.write_bytes(b"invalid gzip")
                else:
                    artifact.write_bytes(gzip.compress(b"synthetic image:amd64"))
                result = self.run_web_consumer()
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(any(command[0] == "push" or command[:2] == ["buildx", "imagetools"] for command in self.commands()))

    def test_tag_move_or_deletion_during_either_web_upload_blocks_version_manifest(self):
        origin, next_sha = self.independent_origin()
        self.web_artifacts()
        for arch in ("amd64", "arm64"):
            for delete in ("", "1"):
                with self.subTest(arch=arch, delete=delete):
                    self.log.unlink(missing_ok=True)
                    subprocess.run(["git", "-C", str(origin), "update-ref", "refs/tags/" + TAG, self.sha], check=True)
                    result = self.run_web_consumer(CHANGE_TAG_AFTER_PUSH=arch, DELETE_FIXTURE_TAG=delete,
                                                   FIXTURE_ORIGIN=str(origin), FIXTURE_TAG=TAG, FIXTURE_NEXT_SHA=next_sha)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertFalse(any(command[:2] == ["buildx", "imagetools"] for command in self.commands()))


class WorkflowAuthorizationTests(unittest.TestCase):
    def test_only_authorized_publisher_jobs_have_write_permission(self):
        for name, publisher, permission in (("releases", "publish-release", "contents"), ("docker", "publish", "packages")):
            with self.subTest(workflow=name):
                data = workflow(name)
                self.assertEqual(data["permissions"], {"contents": "read"})
                self.assertIn("pull_request", data["on"])
                self.assertNotIn("pull_request_target", data["on"])
                self.assertEqual(data["on"]["workflow_dispatch"]["inputs"]["publish"]["default"], "false")
                for key, job in data["jobs"].items():
                    writes = [p for p, value in job.get("permissions", {}).items() if value == "write"]
                    expected_writes = [permission, "actions"] if name == "releases" else [permission]
                    self.assertEqual(writes, expected_writes if key == publisher else [])
                job = data["jobs"][publisher]
                self.assertEqual(job["needs"], ["prepare", "build"])
                expected = "needs.prepare.outputs.publish == 'true'" if name == "docker" else "github.event_name == 'workflow_dispatch' && inputs.publish && needs.prepare.outputs.publish == 'true'"
                self.assertEqual(job["if"], expected)
                # No always()/failure() override: Actions' implicit success() blocks
                # publication when any architecture is failed, skipped or cancelled.
                self.assertNotIn("always(", job["if"])
                self.assertNotIn("failure(", job["if"])

    def test_only_docker_opts_into_published_release_events(self):
        data = workflow("docker")
        self.assertEqual(data["on"]["release"]["types"], ["published"])
        self.assertNotIn("release", workflow("releases")["on"])
        for job in ("prepare", "publish"):
            helpers = [s["run"] for s in data["jobs"][job]["steps"] if "publication.py" in s.get("run", "")]
            self.assertTrue(helpers)
            for script in helpers:
                calls = [line for line in script.splitlines() if "publication.py" in line]
                self.assertTrue(all(line.endswith("prepare --allow-release") for line in calls))

    def test_docker_checks_expected_source_at_prepare_and_all_publication_steps(self):
        data = workflow("docker")
        self.assertEqual(data["on"]["workflow_dispatch"]["inputs"]["expected_sha"]["default"], "")
        for job in ("prepare", "publish"):
            for step in data["jobs"][job]["steps"]:
                if "publication.py" in step.get("run", ""):
                    self.assertEqual(step["env"]["EXPECTED_SOURCE_SHA"], "${{ inputs.expected_sha || '' }}")

    def test_native_dispatch_is_bound_to_the_published_tag_and_success(self):
        steps = workflow("releases")["jobs"]["publish-release"]["steps"]
        self.assertEqual(steps[-2]["name"], "Publish all validated packages")
        dispatch = steps[-1]
        self.assertEqual(dispatch["name"], "Dispatch Docker publication")
        self.assertNotIn("if", dispatch)  # implicit success(); failed releases cannot dispatch
        self.assertEqual(dispatch["env"]["VERSION_TAG"], "${{ github.ref_name }}")
        self.assertEqual(dispatch["env"]["GH_TOKEN"], "${{ secrets.GITHUB_TOKEN }}")

    def test_builders_do_not_log_in_or_publish(self):
        for name in ("releases", "docker"):
            build = workflow(name)["jobs"]["build"]
            for step in build["steps"]:
                self.assertNotIn("login-action", step.get("uses", ""))
                self.assertNotIn("gh release", step.get("run", ""))
                self.assertNotIn("docker push", step.get("run", ""))
                if "build-push-action" in step.get("uses", ""):
                    self.assertEqual(step["with"]["push"], "false")
        self.assertEqual({row["arch"] for row in workflow("docker")["jobs"]["build"]["strategy"]["matrix"]["include"]}, {"amd64", "arm64"})
        self.assertEqual(len(workflow("releases")["jobs"]["build"]["strategy"]["matrix"]["include"]), 6)


if __name__ == "__main__":
    unittest.main()
