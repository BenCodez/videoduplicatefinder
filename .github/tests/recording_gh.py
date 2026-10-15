"""Offline gh fixture: record actual CLI requests and emulate release responses."""

import json
import os
from pathlib import Path
import subprocess
import sys

args = sys.argv[1:]
body = json.load(sys.stdin) if "--input" in args and args[args.index("--input") + 1] == "-" else None
with open(os.environ["COMMAND_LOG"], "a") as log:
    log.write(json.dumps(args) + "\n")
with open(os.environ["COMMAND_LOG"] + ".bodies", "a") as log:
    log.write(json.dumps(body) + "\n")
if args[:3] == ["workflow", "run", "docker.yml"]:
    if os.environ.get("FAIL_DISPATCH"):
        sys.exit(1)
elif "--paginate" in args:
    if os.environ.get("FAIL_LIST"):
        sys.exit(1)
    print(os.environ.get("EXISTING_RELEASES", "[[]]"))
elif "--method" in args and args[args.index("--method") + 1] == "PATCH":
    if os.environ.get("FAIL_PUBLISH"):
        sys.exit(1)
    if os.environ.get("MOVE_TAG_AFTER_PUBLISH"):
        subprocess.run(["git", "-C", os.environ["FIXTURE_ORIGIN"], "update-ref",
                        "refs/tags/" + os.environ["FIXTURE_TAG"], os.environ["FIXTURE_NEXT_SHA"]], check=True)
    print(json.dumps({"id": 753, "draft": False}))
elif any(arg.startswith("https://uploads.github.com/") for arg in args):
    asset = Path(args[args.index("--input") + 1])
    if os.environ.get("FAIL_UPLOAD") == asset.name:
        sys.exit(1)
    if os.environ.get("MOVE_TAG_AFTER_UPLOAD") == asset.name:
        subprocess.run(["git", "-C", os.environ["FIXTURE_ORIGIN"], "update-ref",
                        "refs/tags/" + os.environ["FIXTURE_TAG"], os.environ["FIXTURE_NEXT_SHA"]], check=True)
    print(json.dumps({"name": asset.name, "size": asset.stat().st_size, "state": "uploaded"}))
else:
    if os.environ.get("FAIL_CREATE"):
        sys.exit(1)
    print(json.dumps({"id": 753, "tag_name": body["tag_name"], "draft": True,
                      "upload_url": "https://uploads.github.com/repos/BenCodez/videoduplicatefinder/releases/753/assets{?name,label}"}))
