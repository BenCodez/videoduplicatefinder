# Standalone Web container

Build from the repository root:

```sh
docker build -f VDF.Web/Dockerfile -t vdf-web:local .
docker volume create vdf-web-config
docker volume create vdf-web-state
docker run --detach --name vdf-web \
  --publish 127.0.0.1:8080:8080 \
  --mount type=volume,src=vdf-web-config,dst=/root/.config/VDF \
  --mount type=volume,src=vdf-web-state,dst=/root/.local/state/VDF \
  vdf-web:local
```

Open `http://localhost:8080`. Authentication is enabled by default; the generated
password is printed in `docker logs vdf-web` and saved in the config volume. Treat
these logs and the config volume as credentials. `VDF_WEB_PASSWORD` overrides the
saved password. Login sessions are held in memory, so log in again after a restart.

Add a bind mount for the media directory using a container path such as `/media`
and the `readonly` mount option, then add `/media` to the Web scan paths. Scanning
and thumbnails work with read-only media. Operations that modify media require a
writable mount and corresponding filesystem permissions.

The image listens on port **8080** and preserves the existing mount locations:

| Mount | Contents |
| --- | --- |
| `/root/.config/VDF` | Web settings and generated login credentials |
| `/root/.local/state/VDF` | Scan database and durable automation/review state |

Keep both volumes when recreating or updating a container. A custom database
folder needs its own persistent writable mount. Paths in settings refer to paths
inside the container. Optional AI matching can download its model and native
runtime on first use and requires network access; ordinary scanning uses the
included FFmpeg and FFprobe executables without a first-run download.

The runtime installs distribution FFmpeg for its command-line executables. Web's
optional **Use native FFmpeg binding** setting remains off by default: the
distribution libraries are not guaranteed to match the FFmpeg.AutoGen ABI. The
container checks exercise the default command-line path.

## Runtime validation

The Docker workflow builds and **runs** the image on native AMD64 and ARM64 GitHub
runners for pull requests, branch pushes, tags, and manual validation. It does not
install QEMU. Each runner checks the host, image and running process architecture,
generates synthetic media with the packaged FFmpeg, then verifies authentication,
an interactive scan, duplicate results, JPEG thumbnails and persisted settings,
credentials and database after container recreation. It also enables NFO favourite
protection and selection through the Web UI, then checks a scheduled scan with no
browser connected, restored results after another recreation, and no repeat of
the already claimed schedule slot. The actual Web process gets a read-only media
mount. No production media or host credentials are required.

Run the same check on a Docker host with its native architecture:

```sh
python3 -m venv .venv-web-smoke
.venv-web-smoke/bin/pip install -r .github/scripts/web-container-smoke-requirements.txt
.venv-web-smoke/bin/python -m playwright install --with-deps --only-shell chromium
.venv-web-smoke/bin/python .github/scripts/web_container_smoke.py \
  --image vdf-web:local --arch amd64
```

Use `--arch arm64` on an ARM64 host. The smoke check uses temporary bind mounts,
an ephemeral loopback port mapped to container port 8080, and removes its containers
and files on completion. Run it with a local Docker daemon, since bind mounts must
refer to the same filesystem as the Python process.

Publication remains a separate, manually requested version-tag operation. Runtime
validation must pass for both architectures before an existing publication job can
export/publish the validated images. Automatic checks never push images, and the
workflow does not publish a `latest` tag.

CI supplies the exact checkout SHA and repository URL as OCI image labels and
verifies them before running the smoke check. Local builds label the revision
`unknown` unless built with `--build-arg VDF_SOURCE_REVISION=<commit-sha>`; forks
can also supply `--build-arg VDF_SOURCE_URL=<repository-url>`. Git metadata is
excluded from the build context, so assembly commit metadata can remain
`unknown`; the CI image's OCI revision label identifies its source checkout.
