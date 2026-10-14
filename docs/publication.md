# Build validation and explicit publication

`Build releases` validates GUI, CLI and Web packages for six native targets.
`Docker (Web UI)` validates the Web image on AMD64 and ARM64. Master pushes,
pull requests and version-tag pushes build without publishing. Native packages
are available as workflow artifacts for three days. Existing rolling releases
and their assets are preserved.

Manual runs default to `publish=false`. Publication requires a separate owner
decision and a manual run selecting an existing `vMAJOR.MINOR.PATCH` tag whose
major/minor matches `Directory.Build.props`. The tag must resolve to the exact
workflow commit. Tags predating these workflow changes use their older workflow
definitions; select a new tag containing these changes.

For example, after creating and verifying an appropriate version tag:

```sh
# Build only (the default).
gh workflow run releases.yml --ref v4.1.1 -f publish=false
# Intentionally publish native packages, after all six builds succeed.
gh workflow run releases.yml --ref v4.1.1 -f publish=true
# Separately publish both validated Web image architectures.
gh workflow run docker.yml --ref v4.1.1 -f publish=true
```

Native publication requires all eighteen packages and refuses an existing
release. Failed uploads leave a draft for inspection; no cleanup deletes
existing assets. Only the native publishing job receives `contents: write`.

Web publication uses `ghcr.io/<lowercase-owner>/vdf-web` with version and full
commit SHA tags. Only its publishing job receives `packages: write`. Neither
workflow promotes `latest`. The desktop/VNC container has its own workflow in
the separate Docker repository.
