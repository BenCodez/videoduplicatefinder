# Review persistence and quarantine

Reviews remain version 1 JSON journals. Legacy removal and quarantine history
write version 2, read existing version 1 history, and migrate it on the next save.
Version 2 records include durable proof for protection refresh after a verified
source restoration; older applications refuse this format. Pair an older image
with the complete pre-upgrade configuration/state snapshot when rolling back.
First preserve the complete current state and quarantine destinations, and finish
pending recovery in the newer image. A pre-upgrade snapshot cannot undo later
media mutations or protection decisions: reconcile those before older-image
cleanup, and validate the rollback with read-only media.
These journals do not change `ScannedFiles.db`, its schema, cached fingerprints, or existing database
migrations. JSON metadata is generated at build time for Native AOT. Writes use
unique sibling files, flush file data, and atomically commit **two copies of the
current generation**: the primary and its `.bak` mirror. A successful save does
not return until both contain the current decisions and cleanup intents. Cleanup
cannot move or delete media after an incomplete save, so fallback never drops a
successfully prepared operation. Cancellation is honored before the first namespace
commit; after that boundary the remaining mirror commit is completed. Unrecoverable
write/flush failures still fail the operation before it can mutate media.
Unix journal parents are created one level at a time, and every containing ancestor
is flushed before relying on the child entry, including existing ancestors on retry.
This keeps a previously created but unsuccessfully flushed parent from bypassing
the durability requirement. Unix file-rename directory entries are also flushed; Windows journal and
quarantine-copy commits use `MoveFileEx` with `WRITE_THROUGH`. Unsupported future
versions fail without downgrading the file. Corrupt primaries load a valid backup;
unrecoverable corruption preserves both files and reports an error. Individual
journals are bounded to 32 MiB so an oversized or corrupt journal cannot consume
unbounded memory during loading.

`FileIdentity` captures a normalized path, length, UTC modification/creation
dates, SHA256, a platform file ID (device/inode or volume/file index), and a reliable
change stamp (Unix ctime or Windows file ChangeTime). Linux uses the fixed 256-byte
kernel `statx` layout and libc calls, compatible with glibc and musl without a
Mono native helper. Kernel/libc/filesystem combinations lacking required `statx`
fields fail closed; no architecture-dependent libc `struct stat` is marshaled.
Missing birth time falls back to change time, or modification time if change time
is also unavailable. The initial capture reads
the complete file. Digest memoization requires the same file ID, change stamp,
length, and dates; filesystems without a reliable change stamp always rehash.
Held immutable identities retain their verified snapshots through weak references,
so bounded digest-cache eviction does not cause repeated full hashes during GUI
lookups. Deserialized identities require validation before acquiring this trust.
`CaptureSnapshot` supplies a no-hash token to anchor a displayed file before
delayed full capture; its native ID/change stamp must match the later identity.
Snapshots alone neither validate scan matching nor authorize removal.
Capture and lookup reject symbolic links, reparse points, unsafe ancestors,
unavailable files, and non-regular files. Cancellation-aware `CaptureAsync` keeps
initial large-file hashing off the GUI thread. Performance measurements of the
selection engine alone do not include this initial identity I/O.

`ReviewStore.Find` returns a decision only when its identity still validates.
Decisions for offline files remain stored for future reconciliation but cannot
authorize removal. A rename deliberately invalidates the decision at the new
path: this implementation does **not** relink by hash or apply volatile group IDs
to replacement files. A new decision is required, even for a byte-identical
replacement with copied timestamps. Manual choices/protection, automatic
selection/keeper flags, profile, explanation, and update UTC are preserved.
`SelectionPlanStore` separately saves the full plan as a preview; a stored plan
never authorizes deletion by itself.

Review writers share a path transaction and an exclusive persistent lock file.
GUI updates compare each row's observed journal generation with the current
primary after file capture. Newer decisions at the same path or unrelated paths
are preserved. A conflicting stale choice loads the saved decision; reapply the
choice after reconciliation if it is still intended. Checkbox selection never
implicitly clears a newer manual protection. Each row is reconciled against the generation its own transaction committed,
even if another inspection reloads the shared cache before the UI resumes.
Explicit choices made while saving remain pending for a later save, and verified hardlink keeper refresh reconciles
both identities and foreign decisions before later automatic flushes. A mismatched
file snapshot requires a rescan instead of attaching the decision to another file. First-time initialization
creates an empty journal only when both generations are absent. A damaged or
missing current primary blocks automatic updates and preserves the backup.

`QuarantineService.PreviewAsync` validates and explains the operation without
creating a journal or moving media. The destination must be outside the source and
keeper paths, either an existing accessible directory or one new leaf beneath an
existing accessible parent. Preview reports a planned leaf creation; execution
creates it only after validating both media identities. Missing ancestors are
never recursively created, to avoid masking unavailable mounts. Traversal components, symbolic
links, device paths/alternate streams, overlapping history/media paths, identical
source/keeper paths, and hard-linked keepers are rejected. A remaining keeper must
be an independent, readable file with its original identity, and a protected file
is rejected at the operation boundary.

`QuarantineAsync` is an explicit manual operation and may quarantine different
bytes after GUI review. `QuarantineExactAsync` additionally requires equal lengths
and full SHA256 signatures; automatic callers must use it. Perceptual, partial,
audio, and AI matching do not prove byte identity. Neither API is scheduled or
enabled automatically by the core service.

The operation journals its original, final, copy-staging, and source-holding paths
before mutation. It copies to a unique destination staging file, hashes while
copying, flushes the data, commits the destination without overwriting, and validates
both original and keeper again with fresh hashes. It then atomically claims the
source under a journaled private sibling name, validates the actual claimed file
ID and bytes plus the live keeper again, and removes only the validated claim.
Final verification hashes the keeper first and the source claim last, then checks
both native IDs/change stamps immediately before removal. A writable handle that
changes the source during a long keeper hash or changes the keeper during the final
source hash cancels the operation. Filesystems lacking reliable change stamps cannot
authorize these removal entry points.
Cancellation/failure before source removal retains or restores the original. A
failure after the removal commit point retains the verified quarantine copy and
the last durable journal for recovery. Partial copies are preserved and explicitly
accounted for in history rather than deleted during recovery.

`HistoryAsync` reconciles incomplete operations. A validated interrupted source
claim is restored only to an empty original path; recovery never resumes deletion.
Before a verified claim is restored, history records its native identity and full
bytes. The shared guard then refreshes matching retention decisions at their
recorded paths, including hardlink aliases. A failed or interrupted refresh stays
pending and blocks cleanup of that file and its aliases until recovery succeeds.
Conflicting/replaced paths and offline directories produce `NeedsAttention` and
retain remaining data. A flushed final copy with an absent original can be
recognized as completed quarantine. Incomplete restore operations similarly
retain the quarantine copy and existing files. Complete history is returned as
snapshots. Cleanup operations share an in-process gate, and each quarantine
journal holds an exclusive lock file to reject competing writers for that journal.

`RestoreAsync` verifies the quarantined file identity, copies and hashes into an
original-directory staging file, and atomically renames only to an unoccupied
original path. It retains the verified quarantine copy as recovery data and records
that choice in the history reason. The original parent directory must already be
available; restore does not recreate a missing media mount or overwrite new media.

NFOs, subtitles, and artwork are always left in place. Recognized nearby sidecars
are listed in `SidecarsLeftInPlace`, including shared NFO/artwork, and every reason
states this policy. No sidecar is rewritten or removed.

Filesystem checks cannot reserve a keeper against unrelated external processes
on every supported filesystem. Full identity checks, private source claims, no
overwrite renames, Windows sharing constraints, and conservative recovery reduce
these races; users must avoid concurrent external deletion/mutation of the same
media. Separate application processes using different history files are not
globally coordinated. Tests exercise process interruption through synthetic
journals, cancellation at each pre-removal phase, replacements, unavailable
keepers, unsafe paths, and restore conflicts. Power-loss behavior and every
network filesystem have not been verified by these tests.

Linux scan-time `HardLinkUtils.AreSameFile` retains the existing stat-following
behavior for symbolic aliases when scan settings include reparse links. Review
identities, snapshots, and cleanup retain their no-follow rejection policy.
