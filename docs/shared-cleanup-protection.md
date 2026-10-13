# Shared cleanup protection

The GUI publishes `CleanupProtectionPolicy.json` beside the scan database and
`ReviewedFiles.json`. It contains the current NFO options and folder protection
rules. GUI settings saves and review initialization refresh this versioned,
durable projection. The source settings file is hashed so another frontend cannot
use a projection that predates a settings change.

Every destructive GUI, CLI, and Web cleanup uses the shared protection guard.
The guard validates persisted manual protection and review decisions against the
current file identity, applies folder rules, and reads local NFO markers again
before removal. A group decision to Ignore or Reject blocks every current group
member. A missing, unreadable, or unsupported current policy or review ledger
blocks cleanup; an older backup does not grant permission to remove files.

CLI deletion requires an explicit `--state /path/to/database-folder` (alias
`--db`). For example:

```sh
vdf-cli mark --input results.json --strategy lowest-quality --state /config/vdf --dry-run
vdf-cli mark --input results.json --strategy lowest-quality --state /config/vdf --delete --dry-run false
```

The same explicit state option is required when `scan-and-compare` performs a
cleanup action. The CLI never derives protection state from the results file's
location. A preview without state is labelled unverified. CLI automatic cleanup
requires a live, byte-identical, independent keeper outside the entire selected
batch; perceptual matches require manual review in the GUI.

Removal first records a durable intent, atomically renames the reviewed source to
a private sibling claim, verifies that claim and the keeper, and checks live
protections again. The selected trash or permanent removal mechanism receives
only that verified claim. A failed trash operation restores the source when its
original path is free; it never falls back to permanent deletion. Recovery
restores interrupted claims without overwriting another file and never replays
deletion. Linux trash metadata records the original media path. The Windows
recycle bin may display the private claim name because its API receives that
claim.

If GUI settings failed to load, the GUI preserves the existing policy and blocks
settings saves and cleanup. Restore a valid settings file and reload it or
restart the GUI before saving again. If a current policy or review journal is
damaged, restore its known-good current copy explicitly before cleanup; keep any
retained private claims and recovery copies until their identities are reviewed.
