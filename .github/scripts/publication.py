"""Validate publication intent before handing values to Actions or release tools."""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET


def git(*args):
    return subprocess.check_output(["git", *args], text=True, timeout=30).strip()


def api(*args, body=None):
    return json.loads(subprocess.check_output(["gh", "api", *args], text=True,
                                             input=json.dumps(body) if body is not None else None))


def prepare(*, allow_release=False):
    prefix = ET.parse("Directory.Build.props").findtext(".//VersionPrefix")
    if not prefix or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", prefix):
        raise ValueError("VersionPrefix must be MAJOR.MINOR.PATCH")
    request = os.environ.get("REQUEST_PUBLISH", "false")
    if request not in ("true", "false"):
        raise ValueError("Publication request must be a boolean")
    event = os.environ["GITHUB_EVENT_NAME"]
    manual = event == "workflow_dispatch" and request == "true"
    if request == "true" and not manual:
        raise ValueError("Only a manual run can request publication")
    released = False
    if allow_release and event == "release":
        payload = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
        if payload.get("action") == "published":
            release = payload.get("release", {})
            if release.get("draft") is not False or release.get("tag_name") != os.environ["GITHUB_REF_NAME"]:
                raise ValueError("Published release must match the selected version tag")
            released = True
    publish = manual or released
    version = prefix
    tag = ""
    if os.environ["GITHUB_REF_TYPE"] == "tag":
        tag = os.environ["GITHUB_REF_NAME"]
        if not re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+", tag):
            raise ValueError("Select a vMAJOR.MINOR.PATCH tag")
        version = tag[1:]
        if version.rsplit(".", 1)[0] != prefix.rsplit(".", 1)[0]:
            raise ValueError("Tag major.minor must match VersionPrefix")
    sha = os.environ["GITHUB_SHA"]
    owner = os.environ["GITHUB_REPOSITORY_OWNER"].lower()
    if not re.fullmatch(r"[a-z0-9][a-z0-9-]*", owner):
        raise ValueError("Invalid repository owner")
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError("Expected a full source commit SHA")
    expected_sha = os.environ.get("EXPECTED_SOURCE_SHA", "")
    if expected_sha and (not re.fullmatch(r"[0-9a-f]{40}", expected_sha) or expected_sha != sha):
        raise ValueError("Workflow commit does not match the expected source commit")
    if publish:
        if os.environ["GITHUB_REPOSITORY"].lower() != "bencodez/videoduplicatefinder":
            raise ValueError("Publication is restricted to the canonical repository")
        if not tag:
            raise ValueError("Publication requires a version tag, not a branch")
        if allow_release:
            # Docker's final registry write cannot atomically compare a Git ref.
            # GitHub immutable releases lock the version tag even if the release
            # is later deleted (that tag name cannot be reused).
            release = api(f"repos/BenCodez/videoduplicatefinder/releases/tags/{tag}")
            if (not isinstance(release, dict) or type(release.get("id")) is not int or release["id"] <= 0
                    or release.get("tag_name") != tag or release.get("draft") is not False
                    or release.get("immutable") is not True):
                raise ValueError("Docker publication requires a published immutable release. "
                                 "Enable release immutability in repository Settings before publishing a new version.")
        if git("rev-parse", "HEAD") != sha or git("rev-parse", f"refs/tags/{tag}^{{commit}}") != sha:
            raise ValueError("The selected tag and checkout must match the workflow commit")
        # Checkout can restore a moved tag locally to the original workflow SHA.
        # Query the live remote independently, peeling annotated tags as well.
        ref = f"refs/tags/{tag}"
        remote = dict(reversed(row.split("\t")) for row in git("ls-remote", "--exit-code", "origin", ref, ref + "^{}").splitlines())
        if remote.get(ref + "^{}", remote.get(ref)) != sha:
            raise ValueError("The live source tag no longer matches the workflow commit")
    return {"publish": str(publish).lower(), "version": version, "tag": tag,
            "image": f"ghcr.io/{owner}/vdf-web", "sha": sha}


def publish_native(values, directory):
    if values["publish"] != "true":
        raise ValueError("Publication was not explicitly requested")
    root = Path(directory)
    assets = []
    for target in ("linux-x64", "linux-arm64", "win-x64", "win-arm64", "osx-x64", "osx-arm64"):
        extension = "zip" if target.startswith("win-") else "tar.gz"
        for app in ("GUI", "CLI", "Web"):
            asset = root / f"{app}-{target}.{extension}"
            if asset.is_symlink() or not asset.is_file() or asset.stat().st_size == 0:
                raise ValueError("All eighteen nonempty native packages are required")
            assets.append(str(asset))
    endpoint = "repos/BenCodez/videoduplicatefinder/releases"
    pages = api("--paginate", "--slurp", endpoint + "?per_page=100")
    if not isinstance(pages, list) or any(not isinstance(page, list) for page in pages):
        raise ValueError("Unable to verify existing releases")
    for page in pages:
        for release in page:
            if not isinstance(release, dict) or "tag_name" not in release:
                raise ValueError("Unable to verify existing releases")
            if release["tag_name"] == values["tag"]:
                raise ValueError("A release or draft already exists; inspect it without replacing assets")
    # Address this new release by immutable ID throughout, never resolve by tag.
    # Failed uploads leave this draft for owner inspection; no assets are deleted.
    created = api("--method", "POST", endpoint, "--input", "-", body={
        "tag_name": values["tag"], "target_commitish": values["sha"], "draft": True,
        "make_latest": "false", "name": values["version"], "body": f"Built from commit {values['sha']}."})
    release_id = created.get("id")
    if type(release_id) is not int or release_id <= 0 or created.get("tag_name") != values["tag"] or created.get("draft") is not True:
        raise ValueError("Unexpected newly created release identity")
    upload = f"https://uploads.github.com/{endpoint}/{release_id}/assets"
    if created.get("upload_url") != upload + "{?name,label}":
        raise ValueError("Unexpected release upload endpoint")
    for asset in assets:
        path = Path(asset)
        uploaded = api("--method", "POST", upload + "?name=" + path.name,
                       "--header", "Content-Type: application/octet-stream", "--input", asset)
        if uploaded.get("name") != path.name or uploaded.get("size") != path.stat().st_size or uploaded.get("state") != "uploaded":
            raise ValueError("Release asset upload was not confirmed")
    if prepare() != values:
        raise ValueError("Publication context changed during upload")
    api("--method", "PATCH", f"{endpoint}/{release_id}", "--input", "-",
        body={"draft": False, "make_latest": "false"})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("prepare", "publish-native"))
    parser.add_argument("--assets", default="release-assets")
    parser.add_argument("--allow-release", action="store_true",
                        help="Allow published GitHub releases to publish Docker images")
    args = parser.parse_args()
    if args.allow_release and args.command != "prepare":
        raise ValueError("Automatic release publication is only supported for Docker images")
    values = prepare(allow_release=args.allow_release)
    if args.command == "publish-native":
        publish_native(values, args.assets)
    else:
        # Write only after complete validation; values cannot contain line breaks.
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write("".join(f"{key}={value}\n" for key, value in values.items()))


if __name__ == "__main__":
    main()
