# Automation in the standalone Web app

The standalone Web app is built from `VDF.Web` in this repository. The separate
desktop container repository serves the Avalonia application and is not required
for this Web interface.

On **Scan**, add container-visible media paths and save scan settings. On
**Automation**, configure and save selection, protection and schedules. Local
drafts do not affect cleanup until saved. Conflicting saves preserve the newer
generation; use **Reload saved settings** before reapplying a conflicting edit.

To start over, use **Discard scan results…** on Scan, Results or Automation.
During a scan the action is **Stop scan and discard results…**. Confirm it to
request cancellation, wait for scan processing to finish, then clear the current
and saved result list. Media files, the scan cache, saved reviews and settings
are kept. **Cancel pending discard** cancels the queued clear; it does not resume
the scan. Other file or review operations must finish before results can be cleared.
If Automation is opened during a scan or operation, its settings load automatically
when that operation finishes. Existing unsaved Automation drafts are retained.

Selection supports newest/oldest, quality, strict or weighted folder priorities,
overlapping rules, protected folders, custom criteria and named selection
profiles. Dates retain their stored precision; ties use deterministic path order.
For folder rules, choose **Browse folder…** to navigate the live filesystem.
The browser starts at the rule's existing directory or your first scan path;
scan-path shortcuts let you switch roots. Click a folder to open it, then
**Select** to update the draft. Save automation settings to retain the rule.
In Docker, these are paths inside the container.

On Results, **Apply automation rules** previews the saved selection rules and
checks removal candidates, including after a manual scan. Save draft changes
on Automation first. The saved highlighted-group scope and protections apply;
file operations still require a separate action and confirmation.

The Results selection menu and per-group Keep Best use the shared planner.
Explanations are available in Results and Automation. A selected checkbox is a
candidate for an explicit action, never permission for unattended removal.

Keep, Remove, Ignore, Reject group and manual protection in Automation are durable
reviews tied to the current file identity. Ignore and Reject group veto cleanup
of the entire current duplicate group. A removal choice does not clear manual
protection. Reset the review and remove protection explicitly when intended.
Automatic previews never overwrite manual reviews. Other clients' newer review
generations require refresh before a conflicting choice can be reapplied.

NFO favourites use explicit local XML boolean elements or configured tag values.
Series and season inheritance are optional and bounded. Ratings and watched state
do not imply favourites. There is no live media-server API integration. Unreadable
or uncertain associations retain files conservatively.

Daily, weekly and interval schedules run in the server process with no browser
connected. Calendar times use the container timezone; set `TZ` to the desired
timezone. Intervals use UTC. Scheduling uses saved scan settings, validates the
required folders/tools, durably claims a slot before starting and collapses missed
slots to at most one recent run. Restarting does not repeat an already claimed
slot. **Scan when the web service starts** is a separate opt-in startup scan.
If prerequisites fail before its durable claim, the startup scan remains eligible
for a later server tick after the prerequisite recovers. A claimed run stays
consumed for that service instance even if preparation later fails or is aborted.
Unexpected engine errors are reported as failed scans, including their error
message. Explicit cancellation remains an aborted run.
**Check saved schedule now** displays a failure from that check instead of a
success notice. Checking an idle schedule does not repeat an earlier scan error.
AI schedules require installing the AI components with a manual scan first.

Automatic selection after a scan previews all newly scanned groups. The
highlighted-groups setting scopes manual previews only.

Explicit **Oldest (keep newest)** and **Newest (keep oldest)** actions order by
the configured date source before quality or folder scores, even with weighted
settings. Manual reviews, protected folders and NFO favourites still take
precedence. **Saved selection profile** continues to use the saved scoring mode.

No schedule deletes, quarantines, moves or creates links. A scan holds the shared
busy reservation
through preparation, scanning and result processing; its settings snapshot is
separate from browser edits.

To quarantine, save a destination, select candidates on Results, choose
**Preview quarantine**, inspect each target, remaining keeper and sidecars, then
confirm. A changed destination, target or keeper invalidates that preview.
Quarantine verifies the copy before removing the source and leaves NFOs,
subtitles and artwork in place. History and explicit restore are on Automation.
Restore verifies the retained copy and never overwrites an occupied original
path. Startup recovery restores interrupted claims; it never resumes deletion.
Delete, trash and link replacement continue to use live shared protection checks
and require a current independent unselected keeper.

The default persistent locations remain `/root/.config/VDF` and
`/root/.local/state/VDF` in the container. New files include
`web-settings.automation.json` in config, and `WebSchedule.json`, `WebResults.json`
and `SelectionPlan.json` beside the scan database. Results preserve their original
native snapshots across restart; replacements do not inherit old cleanup
authority. Reviews and cleanup history use the existing Core journals without
changing the scan database schema. Preserve all state, backups and quarantine
destinations together. See [the shared recovery guidance](review-cleanup.md) and
[container setup and validation](web-container.md).

Results and selection previews each have a 32 MiB serialized save limit. A scan
that exceeds this limit still completes and keeps the complete results and
preview available in the running service. Scan, Results and Automation show a
warning that the oversized data lasts only until service restart. Saved copies
are replaced with an empty generation so older results cannot reappear. After
restarting, run a new scan and prepare the preview again. This fallback applies
only to the size limit; corrupt journals and other save failures still report an
error. A subsequent successful save clears the corresponding warning.

After a restart, an abandoned scheduled run is marked **Interrupted** while its
claimed slot remains recorded, so that slot is not repeated. A live cooperating
process keeps its run status. Preserve the persistent lock files, including
`WebSchedule.run.lock`; do not remove them while an instance is running.
If another process holds the run lease before publishing its claim, a schedule
check skips without reporting a scan failure. Unclaimed startup work remains
eligible after the lease is released; unsafe lock paths and journal errors still
require repair.
Manual scans use the same lifetime lease, even without automation settings, and
report a busy database folder rather than overlapping another scan. Completion
or cancellation during preparation retains the lease until startup unwinds.
Saved results record whether the scan completed, including scans with no
duplicates. A reset or an interrupted replacement scan restores the Scan page
as idle. Older empty snapshots without completion metadata also restore as idle;
older nonempty snapshots continue to restore as completed results.
Saved previews belong to their result generation. Replacing, resetting, clearing
or dismissing results prevents an earlier preview from automatically selecting
the new generation after a restart. A matching saved preview is recomputed using
current settings and reviews within its saved group scope. Per-group and
highlighted-group previews do not expand to other groups after restarting;
automatic post-scan previews retain all groups. An explicitly chosen preview profile
also survives restart; previews using saved defaults follow the current saved profile.
Existing version-one snapshots and previews
without generation fields remain readable together; newly saved generations do
not inherit an unbound older preview. The shared scan database format is unchanged.
Result invalidation, completion saves and explicit resets are independent of the
optional automation configuration. Missing established protection settings still
require restoration; scanning does not replace them with permissive defaults.
Clear Database empties only the selected scan database, and invalidates saved
results in both that folder and the folder that produced the visible rows.
If either result save fails, the database and visible rows are retained for retry;
Reset clears only the visible result generation and leaves scan databases intact.
The saved origin remains tracked after a failed database load revokes cleanup
authority; restoring storage does not reauthorize file operations in the running
service without a rescan.
Completed results are saved before optional settings reload or selection
processing, so restoring settings after a reported configuration error retains
the completed scan output.

For an upgrade, stop the old container and make copies of both persistent mounts.
Test the new image with those copies, another port and read-only media. Keep the
old image and untouched original state for rollback. Restore the entire matching
state snapshot when testing an older image; later media changes and newer review
protections require reconciliation before permitting old cleanup again. Updating
only a settings or database file is insufficient. Do not run two containers
against the same writable state during migration.
