# Improved media review in the existing desktop/browser interface

This fork retains Avalonia, the existing VDF scanner, cached fingerprints, comparison
windows and jlesage browser/VNC access. It adds keeper recommendations, local NFO
protection, saved reviews and recovery-oriented cleanup. No media service API is required.

## Selection and dates

Open **Selection, favourites and recovery** from Settings or the selection menu.
The improved **Keep newest**/**Keep oldest** commands describe the copy to retain.
Legacy **Check newest** means mark newer copies for removal and keep the oldest;
**Check oldest** means mark older copies and keep the newest. Legacy commands follow
the results date choice; improved commands default to last modification time.

Previously the legacy command scope changed implicitly when several rows were
highlighted. A results filter also excluded candidates, and checked state from earlier
commands could survive outside that scope. Improved planning defaults to complete
result groups, including filtered rows. An explicit highlighted-group setting scopes
manual planning; scheduled selection always considers all results.

File creation time can describe a copy/import rather than the recording date. Choose
modification, creation or available embedded creation metadata explicitly. Internally
dates use UTC; absent/invalid dates lose to known dates and do not count as the oldest
copy. Identical criteria use ordinal path order. Rows explain the date and deciding
rule. An embedded date is metadata, not proof of when a video was filmed.

Strict mode normally chooses the highest configured numeric folder priority before
quality or dates; this includes the default FolderPriorityFirst and the NewestFirst,
OldestFirst and Custom profiles. Explicitly choosing QualityFirst instead applies the
configured quality criteria in order across folders, then uses folder priority when
those criteria tie, followed by ordinal path order. With no quality criteria configured,
QualityFirst falls back to folder priority. In weighted mode every profile combines the
folder weight with normalized criterion weights; QualityFirst does not override that
scoring. Protection, manual Keep decisions and group Ignore/Reject decisions still take
precedence. Size is not a default quality signal. Custom criteria can prefer lower values.

Folder rules use absolute paths and directory boundaries. Linux comparisons are case
sensitive. Nested rules can use the most specific or highest-priority match; protection
is always the union of matching protected rules. Optional wildcard patterns match the
path relative to the rule's directory. Invalid enabled protection rules block cleanup.

Protection overrides every score. All favourite/protected copies remain, and an offline
protected copy never justifies removing the last available copy. Perceptual and AI
matches require review even at 100% thumbnail similarity; this is not a byte hash.

## Local NFO favourites

Enable NFO protection and test a representative media path before relying on it.
Configure boolean element names and tag element/value markers. Initial examples are
`favorite`, `isfavorite`, and `tag` values `Favorite`/`Favourite`. Ratings, watched state
and user ratings never imply favourite status. Standard Emby NFO exports may not include
user favourites; compatibility with a particular export requires testing its markers.

Same-basename sidecars are preferred. A directory-wide `movie.nfo` applies only when
one video makes the association unambiguous. Series/season inheritance is opt-in and
bounded. Ambiguous associations are diagnosed; invalid or unreadable applicable NFOs
block automatic cleanup for review. NFO files are read only. XML DTDs and external
entities are rejected, and parsing/caches have resource limits.

See [NFO details](nfo-protection.md) for association and cache behavior.

## Reviews and restart behavior

Checking/unchecking a file records a deliberate remove/keep decision. Use the row
actions to protect, unprotect, ignore/reject a group or reset its review choices.
Automatic reruns replace prior automatic choices while preserving manual choices.
An explicit checkbox change can replace your previous manual Keep/Remove choice;
hard manual, folder, and NFO protections still block removal. Group Ignore/Reject
choices must be reset before marking members again.

Reviews and plans are separate versioned JSON journals beside the existing scan database.
Writes use flushed temporary files and atomic replacement, with recovery backups.
File identity includes filesystem identity, timestamps and a content digest. Replacement
files cannot inherit old removal decisions. Renamed files require review again rather
than guessing a moved-file association. Initial identity capture reads the file; known
unchanged filesystem snapshots reuse digests where a reliable change stamp is available.
Temporary read failures retain saved protection and block selection or cleanup until
the file can be validated again; they are not treated as evidence of replacement.

Legacy backup checkmarks without validated review identities are cleared on import;
review those results again. Current review journals restore decisions after validating
the file, including when duplicate group IDs change.

## Scheduled scanning

The existing Schedule Settings section supports daily/weekly calendar schedules and
periodic reconciliation, a scan profile, Run Now, last outcome/duration and next run.
Calendar times follow the application/container timezone (`TZ` in Docker). A claimed
slot is saved before scanning. Restart does not duplicate a claimed job; at most one
recent missed slot runs, according to the catch-up window. A failed/cancelled slot is
not retried repeatedly; use Run Now or wait for the next slot.

Scans run inside the desktop process while the container is running, independently of
browser connections. Manual and scheduled starts share a gate covering preparation,
scan, comparison and selection; preview work must also finish before the next scan.
The existing fingerprint database avoids decoding unchanged files. NFO state refreshes
when results are evaluated. Periodic reconciliation handles network mounts; filesystem
event watching is not implemented in this version.

Automatic selection is disabled by default. Enabling it only checks removal candidates;
there is no unattended deletion or quarantine scheduler.

## Cleanup and restoration

Preview before a separate cleanup action. All disk deletion/link-replacement entry points
recheck protection, file identity and a distinct available unselected keeper. Stale results
need a rescan. A failed trash operation preserves the original; it never falls back to
permanent deletion. Permanent deletion is an explicit separate action.

Legacy destructive operations first journal and atomically claim the source to prevent a
replacement at the original path from being deleted. Interrupted claims are recovered
conservatively without overwriting another file. The OS trash may display the temporary
claim name on Windows; the journal records the original path. macOS Trash preserves
the media filename, and Linux trash metadata preserves its name and encoded restore
path. Prefer quarantine for integrated restore.

Hardlink replacement refreshes the keeper's verified identity between batch actions.
It refuses a keeper with a deliberate manual protection, Keep, Ignore or Reject decision:
creating a hardlink changes filesystem identity metadata, and a crash before saving
that change could invalidate its saved protection. Use quarantine for those keepers.
Automatic keeper recommendations alone do not impose this restriction.

Quarantine stages and verifies a copy before claiming/removing the original, including
across filesystems. An available keeper must remain. History records outcomes and restore
does not overwrite existing destinations. Sidecars, subtitles and artwork remain at their
original locations, including shared files; they are accounted for rather than silently
moved. A recovery copy remains in quarantine after restore. Manual quarantine of a
perceptual match needs explicit review; automatic exact-only APIs require matching SHA-256.

Use synthetic media and a copied configuration when validating a Docker update. See the
Docker fork's deployment and rollback guide before changing a production image.

## Upstream and licensing

The application remains under its existing AGPLv3 license and attribution. Keep `origin`
pointed to this fork and an `upstream` remote to `0x90d/videoduplicatefinder`; merge upstream
changes on a testing branch, run the tests, and audit the Docker patches before changing
the pinned application revision. Docker wrapper licensing and jlesage attribution remain
in the wrapper repository. Publish the corresponding fork source with binary releases.
