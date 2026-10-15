// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VDF.Core;
using VDF.Core.Cleanup;
using VDF.Core.FFTools;
using VDF.Core.Nfo;
using VDF.Core.Review;
using VDF.Core.Scheduling;
using VDF.Core.Selection;
using VDF.Core.Utils;
using VDF.Core.ViewModels;

namespace VDF.Web.Services;

public sealed partial class ScanService {
    WebAutomationStore AutomationStore => new(Path.Combine(Path.GetDirectoryName(WebSettingsService.SettingsPath)!,
        Path.GetFileNameWithoutExtension(WebSettingsService.SettingsPath) + ".automation.json"));
    public string AutomationStatus { get; private set; } = "";
    public SelectionPlan? LastSelectionPlan { get; private set; }
    public string? ResultsPersistenceWarning { get; private set; }
    public string? SelectionPersistenceWarning { get; private set; }
    public WebScheduleState? ScheduleState { get; private set; }
    long _automationGeneration;
    public long AutomationGeneration => Interlocked.Read(ref _automationGeneration);
    void NotifyAutomationChanged() { Interlocked.Increment(ref _automationGeneration); Notify(); }
    Task<WebAutomationView>? _automationViewRead;
    WebAutomationConfiguration? _runAutomation;
    string? _scheduledStateFolder;
    bool _startupChecked;
    (string Folder, string Revision, Dictionary<string, QuarantinePreview> Targets)? _quarantinePreview;
    internal Func<Task>? BeforeReviewCommitAsync { get; set; }
    internal Action? StartSearchOverride { get; set; }
    internal Task CompletionTask { get; private set; } = Task.CompletedTask;

    string ConfiguredStateFolder(Settings? settings = null) {
        settings ??= Settings;
        if (!string.IsNullOrEmpty(settings.CustomDatabaseFolder) && !Directory.Exists(settings.CustomDatabaseFolder))
            throw new DirectoryNotFoundException("The configured database folder is unavailable. Restore it before continuing.");
        string folder = CoreUtils.ResolveDatabaseFolder(settings.CustomDatabaseFolder);
        if (string.IsNullOrEmpty(settings.CustomDatabaseFolder)) SafePaths.EnsureParentDirectory(Path.Combine(folder, "placeholder"));
        SafePaths.RequireDirectory(folder);
        return folder;
    }
    string ResultsStateFolder() => _resultStateFolder ?? throw new IOException("Rescan before reviewing or removing these results.");
    void ReserveAutomationWork() {
        if (!TryBeginDataWork()) throw new InvalidOperationException("Wait for the current scan or operation to finish.");
    }
    async Task<WebAutomationConfiguration> LoadAutomationCoreAsync(string folder) {
        var store = AutomationStore;
        var config = await store.LoadAsync();
        if (config.Revision.Length == 0) {
            string policy = Path.Combine(folder, CleanupProtectionPolicyStore.FileName);
            if (SafePaths.Exists(policy) || SafePaths.Exists(policy + ".bak")) {
                // Initial Web controls must preserve established shared folder
                // and NFO protections rather than replace them with defaults.
                var established = await CleanupProtectionPolicyStore.LoadAsync(folder);
                config.Nfo = established.NfoOptions;
                config.Selection.FolderRules = established.FolderRules;
                config.Selection.OverlapPolicy = established.OverlapPolicy;
            }
            config = await store.SaveAsync(config, folder);
        }
        else await store.ProjectAsync(config, folder);
        return config;
    }
    public async Task<WebAutomationConfiguration> LoadAutomationAsync() {
        ReserveAutomationWork();
        try { return await LoadAutomationCoreAsync(ConfiguredStateFolder()); }
        finally { EndDataWork(automationChanged: false); }
    }
    public async Task<WebAutomationConfiguration> SaveAutomationAsync(WebAutomationConfiguration config) {
        ReserveAutomationWork();
        try {
            // Protections also apply to currently visible results if the database
            // preference has since changed. Never silently borrow another folder.
            string folder = _resultStateFolder ?? ConfiguredStateFolder();
            var saved = await AutomationStore.SaveAsync(config, folder);
            ClearSelectionPlan();
            _quarantinePreview = null;
            AutomationStatus = "Web automation settings saved.";
            return saved;
        }
        finally { EndDataWork(); }
    }

    async Task<ReviewStore> CurrentReviewStoreAsync(string folder) {
        var store = new ReviewStore(Path.Combine(folder, "ReviewedFiles.json"));
        await store.LoadForCleanupAsync();
        return store;
    }
    static string RevisionOf(ReviewRecord? record) => record is null ? "" : Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, ReviewTokenJsonContext.Default.ReviewRecord))));

    public async Task<IReadOnlyList<WebReviewRow>> GetReviewRowsAsync() {
        ReserveAutomationWork();
        try { return await GetReviewRowsCoreAsync(); }
        finally { EndDataWork(automationChanged: false); }
    }
    async Task<IReadOnlyList<WebReviewRow>> GetReviewRowsCoreAsync() {
            string folder = ResultsStateFolder();
            await LoadAutomationCoreAsync(folder);
            var reviews = await CurrentReviewStoreAsync(folder);
            var byPath = reviews.Records.ToDictionary(r => r.Identity.Path, PathComparer.ForCurrentPlatform);
            var guard = new CleanupProtectionGuard(folder);
            var rows = new List<WebReviewRow>();
            foreach (var item in Duplicates.ToList()) {
                var identity = CaptureReviewedIdentity(item);
                var record = byPath.GetValueOrDefault(item.Path);
                bool matching = identity is not null && (record is null || identity.Matches(record.Identity));
                var protection = identity is null ? new CleanupProtectionCheck(true, "File changed or is unavailable; rescan.") : await guard.InspectAsync(identity);
                rows.Add(new(item, matching ? record?.Choice ?? ReviewChoice.None : ReviewChoice.None,
                    matching && record?.ManuallyProtected == true, RevisionOf(record),
                    !matching ? "The saved review has another identity. Rescan before changing it." :
                        protection.IsProtected ? protection.Reason : record?.Reason ?? "Not reviewed", protection.IsProtected || !matching));
            }
            return rows;
    }

    /// <summary>One read reservation covers the complete view. Other tabs cannot
    /// interleave between review and history phases and keep each other busy.</summary>
    public Task<WebAutomationView> GetAutomationViewAsync() {
        lock (_operationSync) {
            // Multiple circuits can request the same refresh after a write.
            // Share the in-flight snapshot, rather than making one circuit fail
            // halfway through the other's already committed review action.
            if (_automationViewRead is { IsCompleted: false }) return _automationViewRead;
            return _automationViewRead = ReadAutomationViewAsync();
        }
    }
    async Task<WebAutomationView> ReadAutomationViewAsync() {
        ReserveAutomationWork();
        try {
            var history = await new CleanupProtectionGuard(_resultStateFolder ?? ConfiguredStateFolder()).QuarantineHistoryAsync();
            IReadOnlyList<WebReviewRow> rows = Duplicates.Count == 0 ? Array.Empty<WebReviewRow>() : await GetReviewRowsCoreAsync();
            return new(rows, history);
        }
        finally { EndDataWork(automationChanged: false); }
    }

    public async Task SetReviewAsync(DuplicateItem item, ReviewChoice choice, bool? protectedFile, string expectedRevision) {
        if (!Enum.IsDefined(choice)) throw new ArgumentException("Invalid review choice.");
        ReserveAutomationWork();
        try {
            string folder = ResultsStateFolder();
            await LoadAutomationCoreAsync(folder);
            var identity = CaptureReviewedIdentity(item) ?? throw new IOException("The reviewed file changed. Rescan first.");
            if (!Duplicates.Contains(item)) throw new IOException("The reviewed result is no longer available.");
            var store = await CurrentReviewStoreAsync(folder);
            if (BeforeReviewCommitAsync is not null) await BeforeReviewCommitAsync();
            await store.UpdateAsync(current => {
                var merged = current.ToDictionary(r => r.Identity.Path, PathComparer.ForCurrentPlatform);
                var previous = merged.GetValueOrDefault(identity.Path);
                if (RevisionOf(previous) != expectedRevision)
                    throw new InvalidOperationException("A newer review was saved. Reload and reapply this choice if still intended.");
                if (!identity.MatchesCurrent(true)) throw new IOException("The file changed before saving its review.");
                merged[identity.Path] = new() { Identity = identity, Choice = choice,
                    ManuallyProtected = protectedFile ?? (previous is not null && identity.Matches(previous.Identity) && previous.ManuallyProtected),
                    Reason = "Manual Web review: " + choice, IsKeeper = choice == ReviewChoice.Keep, UpdatedUtc = DateTime.UtcNow };
                return merged.Values;
            });
            AutomationStatus = "Review saved. Automatic selection preserves deliberate choices.";
            ClearSelectionPlan();
        }
        finally { EndDataWork(); }
    }

    public async Task<(SelectionPlan Plan, HashSet<Guid>? Scope)> PreviewResultsSelectionAsync(
        SelectionProfile? profile, HashSet<Guid>? explicitScope, HashSet<Guid> highlightedGroups) {
        ReserveAutomationWork();
        try {
            ClearSelectionPlan();
            var config = await LoadAutomationCoreAsync(ResultsStateFolder());
            var scope = explicitScope ?? (config.Selection.HighlightedGroupsOnly ? highlightedGroups : null);
            if (scope is { Count: 0 })
                throw new InvalidOperationException("Select at least one file to highlight its group before previewing selection.");
            return (await PreviewSelectionCoreAsync(config, profile, scope), scope);
        }
        finally { EndDataWork(); }
    }
    public async Task<SelectionPlan> PreviewSelectionAsync(SelectionProfile? profile = null, IEnumerable<Guid>? groups = null) {
        ReserveAutomationWork();
        try {
            ClearSelectionPlan();
            return await PreviewSelectionCoreAsync(await LoadAutomationCoreAsync(ResultsStateFolder()), profile, groups);
        }
        finally { EndDataWork(); }
    }
    async Task<SelectionPlan> PreviewSelectionCoreAsync(WebAutomationConfiguration config, SelectionProfile? profile = null,
        IEnumerable<Guid>? groups = null) {
        ClearSelectionPlan();
        string folder = ResultsStateFolder();
        var options = JsonSerializer.Deserialize(JsonSerializer.Serialize(config.Selection, WebAutomationJsonContext.Default.SelectionOptions),
            WebAutomationJsonContext.Default.SelectionOptions)!;
        if (profile is { } value) options.Profile = value;
        var scope = groups?.ToHashSet();
        if (options.HighlightedGroupsOnly && scope is null)
            throw new InvalidOperationException("Choose the groups to preview or turn off selected-groups-only scope.");
        var reviews = await CurrentReviewStoreAsync(folder);
        var byPath = reviews.Records.ToDictionary(r => r.Identity.Path, PathComparer.ForCurrentPlatform);
        var guard = new CleanupProtectionGuard(folder);
        var nfo = new NfoFavouriteService();
        var candidates = new List<SelectionCandidate>();
        foreach (var item in Duplicates.ToList().Where(i => scope is null || scope.Contains(i.GroupId))) {
            var identity = CaptureReviewedIdentity(item);
            var review = byPath.GetValueOrDefault(item.Path);
            bool stale = review is not null && (identity is null || !identity.Matches(review.Identity));
            var local = nfo.Inspect(item.Path, config.Nfo);
            var live = identity is null ? new CleanupProtectionCheck(true, "File changed or is unavailable.") : await guard.InspectAsync(identity);
            candidates.Add(new(item) { IsAvailable = identity is not null, ManuallyProtected = live.IsProtected,
                NfoProtected = local.IsFavourite, ProtectionUncertain = local.IsUncertain || stale,
                ProtectionReason = stale ? "Saved review identity changed; rescan." : live.IsProtected ? live.Reason : local.Reason,
                ManualChoice = !stale ? review?.Choice ?? ReviewChoice.None : ReviewChoice.None,
                EmbeddedDateUtc = options.DateSource == SelectionDateSource.Embedded ? FFProbeEngine.GetCreationTime(item.Path) : null });
        }
        var plan = SelectionPlanner.BuildPlan(candidates, options);
        // Persist explanations separately. Automatic previews never upsert manual
        // reviews: a concurrent Keep/Protect cannot be lost to an autosave.
        await SaveSelectionPlanForSessionAsync(folder, plan);
        AutomationStatus = $"{plan.Decisions.Count(d => d.RemoveCandidate)} removal candidates. Review before explicit cleanup.";
        return plan;
    }
    // Only the known serialized-size failure is recoverable here. Corruption,
    // unsupported schemas, leases and IO errors must still fail visibly.
    static bool IsJournalSizeLimit(InvalidDataException ex) =>
        ex.Message == "The journal exceeds the supported size limit.";
    internal async Task SaveSelectionPlanForSessionAsync(string folder, SelectionPlan plan) {
        string path = Path.Combine(folder, "SelectionPlan.json");
        using var lease = JournalWriteLease.Acquire(path);
        await RequireSupportedJournalCopiesAsync(path, "selection preview");
        try {
            await SelectionPlanStore.SaveAsync(path, plan);
            SelectionPersistenceWarning = null;
        }
        catch (InvalidDataException ex) when (IsJournalSizeLimit(ex)) {
            // Replace both saved copies: an older explanation must not reappear
            // after restart when this session's preview cannot be persisted.
            await SelectionPlanStore.SaveAsync(path, new SelectionPlan());
            SelectionPersistenceWarning = "This selection preview exceeds the 32 MiB save limit. It is available only until the service restarts; prepare the preview again after restarting.";
        }
        LastSelectionPlan = plan;
    }
    void ClearSelectionPlan() {
        LastSelectionPlan = null;
        SelectionPersistenceWarning = null;
    }

    public async Task<IReadOnlyList<QuarantineRecord>> GetQuarantineHistoryAsync() {
        ReserveAutomationWork();
        try { return await new CleanupProtectionGuard(_resultStateFolder ?? ConfiguredStateFolder()).QuarantineHistoryAsync(); }
        finally { EndDataWork(automationChanged: false); }
    }
    public async Task RestoreQuarantineAsync(string operationId) {
        ReserveAutomationWork();
        try {
            var guard = new CleanupProtectionGuard(_resultStateFolder ?? ConfiguredStateFolder());
            await guard.CreateQuarantineService().RestoreAsync(operationId);
            ClearSelectionPlan();
            AutomationStatus = "Quarantine restored. Rescan to include the restored file in results.";
        }
        finally { EndDataWork(); }
    }

    async Task<string> RequireResultsDatabaseAsync() {
        string folder = ResultsStateFolder();
        if (!PathComparer.ForCurrentPlatform.Equals(NormalizeDatabaseFolder(DatabaseUtils.GetDatabaseFolderPath()),
            NormalizeDatabaseFolder(folder)) && !await LoadDatabaseWithResultsAuthorityAsync(folder)) {
            ClearCleanupSnapshots();
            throw new IOException("The results database could not be loaded; rescan before cleanup.");
        }
        return folder;
    }
    (FileIdentity Target, FileIdentity Keeper, List<FileIdentity> Group) QuarantinePair(DuplicateItem item, HashSet<string> requested) {
        if (!Duplicates.Contains(item)) throw new IOException("This file is no longer in the results.");
        var target = CaptureReviewedIdentity(item) ?? throw new IOException("The selected file changed or is unavailable. Rescan first.");
        var group = Duplicates.Where(r => r.GroupId == item.GroupId).Select(r => (Row: r,
            Identity: CaptureReviewedIdentity(r) ?? throw new IOException("A group member changed or is unavailable. Rescan first."))).ToList();
        var keeper = group.Where(r => !requested.Contains(r.Row.Path) && r.Identity.StableFileId != target.StableFileId)
            .OrderByDescending(r => r.Row.Similarity).ThenBy(r => r.Row.Path, StringComparer.Ordinal).FirstOrDefault().Identity;
        if (keeper is null) throw new IOException("This group has no independent unselected keeper.");
        return (target, keeper, group.Select(r => r.Identity).ToList());
    }
    public async Task<IReadOnlyList<QuarantinePreview>> PreviewQuarantineAsync(IEnumerable<DuplicateItem> items) {
        var selected = items.Distinct().ToList();
        ReserveAutomationWork();
        try {
            string folder = await RequireResultsDatabaseAsync();
            var config = await LoadAutomationCoreAsync(folder);
            if (config.QuarantineDirectory.Length == 0) throw new InvalidOperationException("Choose and save a quarantine directory in Automation first.");
            var guard = new CleanupProtectionGuard(folder);
            var paths = selected.Select(r => r.Path).ToHashSet(PathComparer.ForCurrentPlatform);
            var previews = new List<QuarantinePreview>();
            foreach (var item in selected) {
                var pair = QuarantinePair(item, paths);
                await guard.ValidateGroupAsync(pair.Group);
                await guard.ValidateRemovalAsync(pair.Target, pair.Keeper);
                previews.Add(await guard.CreateQuarantineService().PreviewAsync(pair.Target, pair.Keeper, config.QuarantineDirectory));
            }
            _quarantinePreview = (folder, config.Revision, previews.ToDictionary(p => p.Target.Path, p => p, PathComparer.ForCurrentPlatform));
            return previews;
        }
        finally { EndDataWork(); }
    }
    public async Task<FileOpResult> QuarantineItemsAsync(IEnumerable<DuplicateItem> items) {
        var selected = items.Distinct().ToList();
        var result = new FileOpResult();
        if (!TryBeginFileOp("Quarantining", selected.Count, result)) return result;
        try {
            string folder = await RequireResultsDatabaseAsync();
            var config = await LoadAutomationCoreAsync(folder);
            if (config.QuarantineDirectory.Length == 0) throw new InvalidOperationException("Choose and save a quarantine directory first.");
            var preview = _quarantinePreview;
            _quarantinePreview = null;
            if (preview is null || preview.Value.Revision != config.Revision ||
                !PathComparer.ForCurrentPlatform.Equals(preview.Value.Folder, folder) || selected.Count != preview.Value.Targets.Count ||
                selected.Any(item => !preview.Value.Targets.TryGetValue(item.Path, out var proof) || !proof.Target.MatchesCurrent(true)))
                throw new InvalidOperationException("The quarantine preview changed or expired. Preview these files again before confirming.");
            var guard = new CleanupProtectionGuard(folder);
            await guard.RecoverAsync();
            var paths = selected.Select(r => r.Path).ToHashSet(PathComparer.ForCurrentPlatform);
            foreach (var item in selected) {
                try {
                    var pair = QuarantinePair(item, paths);
                    if (!preview.Value.Targets[item.Path].Keeper.Matches(pair.Keeper))
                        throw new IOException("The previewed keeper changed. Preview quarantine again before confirming.");
                    await guard.QuarantineAsync(pair.Target, pair.Keeper, config.QuarantineDirectory, pair.Group);
                    _engine.Duplicates.Remove(item);
                    _resultSnapshots.Remove(item);
                    ScanEngine.RemoveFromDatabase(new FileEntry { Path = item.Path });
                    result.Done++;
                    result.FreedBytes += pair.Target.Length;
                }
                catch (Exception ex) { result.Failed++; result.Errors.Add(ex.Message); }
                finally { FileOpCurrent++; Notify(); }
            }
            if (result.Done > 0) {
                try { ScanEngine.SaveDatabase(); }
                catch (Exception ex) { result.Errors.Add("Quarantine completed, but the database save failed: " + ex.Message); }
            }
            DropSingletonGroups();
            ClearSelectionPlan();
            await PersistResultsAsync();
        }
        catch (Exception ex) { result.Failed = selected.Count - result.Done; result.Errors.Add(ex.Message); }
        finally { EndFileOp(); }
        return result;
    }

    static bool HasSavedResultsJournal(string folder) => SafePaths.Exists(Path.Combine(folder, "WebResults.json")) ||
        SafePaths.Exists(Path.Combine(folder, "WebResults.json.bak"));
    async Task InvalidatePersistedResultsForScanAsync() {
        string nextFolder = CoreUtils.ResolveDatabaseFolder(_engine.Settings.CustomDatabaseFolder);
        if (_runAutomation is null && _resultStateFolder is null && !HasSavedResultsJournal(nextFolder)) return;
        // Commit the discarded generation before preparation or the engine can
        // fail, be interrupted, or change its database. Both journal copies must
        // be empty so restart cannot resurrect results this scan replaced.
        nextFolder = ConfiguredStateFolder(_engine.Settings);
        await SaveResultsAsync(nextFolder, new());
        if (_resultStateFolder is { } previous && !PathComparer.ForCurrentPlatform.Equals(
            NormalizeDatabaseFolder(previous), NormalizeDatabaseFolder(nextFolder))) {
            SafePaths.RequireDirectory(previous);
            await SaveResultsAsync(previous, new());
        }
        ResultsPersistenceWarning = null;
    }
    async Task PersistResultsAsync() {
        if (_resultStateFolder is null) return;
        bool saved = await SaveResultsAsync(_resultStateFolder, Duplicates.Select(item => new WebSavedResult {
            Item = item, Snapshot = _resultSnapshots.GetValueOrDefault(item) }).ToList(), allowSizeFallback: true, completed: true);
        ResultsPersistenceWarning = saved ? null : "These results exceed the 32 MiB save limit. They are available only until the service restarts; run a new scan after restarting to recreate the results.";
    }
    static async Task<bool> SaveResultsAsync(string folder, List<WebSavedResult> rows, bool allowSizeFallback = false, bool completed = false) {
        SafePaths.RequireDirectory(folder);
        string path = Path.Combine(folder, "WebResults.json");
        using var lease = JournalWriteLease.Acquire(path);
        await RequireSupportedJournalCopiesAsync(path);
        try {
            await DurableJsonFile.WriteAsync(path, new WebResultsEnvelope { Rows = rows, Completed = completed },
                WebAutomationJsonContext.Default.WebResultsEnvelope, ValidateResults);
            return true;
        }
        catch (InvalidDataException ex) when (allowSizeFallback && IsJournalSizeLimit(ex)) {
            // Keep the writer lease through the fallback. Preserve complete
            // in-memory results; restart must not revive an older generation.
            await DurableJsonFile.WriteAsync(path, new WebResultsEnvelope { Completed = false },
                WebAutomationJsonContext.Default.WebResultsEnvelope, ValidateResults);
            return false;
        }
    }
    public async Task ResetAsync() {
        ReserveAutomationWork();
        try {
            if (_resultStateFolder is { } folder) await SaveResultsAsync(folder, new());
            ResultsPersistenceWarning = null;
            _engine.Duplicates.Clear();
            ClearCleanupSnapshots();
            ClearThumbnailCaches();
            ClearSelectionPlan();
            _quarantinePreview = null;
            State = ScanState.Idle;
            ErrorMessage = null;
            LastProgress = null;
            FilesHashed = 0;
        }
        finally { EndDataWork(); }
    }
    public async Task RemoveFromResultsAsync(IEnumerable<DuplicateItem> items) {
        var selected = items.ToList();
        ReserveAutomationWork();
        try {
            foreach (var item in selected) { _engine.Duplicates.Remove(item); _resultSnapshots.Remove(item); }
            DropSingletonGroups();
            ClearSelectionPlan();
            _quarantinePreview = null;
            await PersistResultsAsync();
        }
        finally { EndDataWork(); }
    }
    async Task PersistFileOperationResultsAsync(FileOpResult result) {
        try { await PersistResultsAsync(); }
        catch (Exception ex) { result.Errors.Add("File operations completed, but saving the results failed: " + ex.Message); }
    }
    static void RequireJournalVersion(int version, string journal = "Web results") {
        if (version != 1) throw new NotSupportedException($"The {journal} schema is unsupported; the files were preserved.");
    }
    static async Task RequireSupportedJournalCopiesAsync(string path, string journal = "Web results") {
        // Check both copies before typed row parsing can mistake a future
        // payload for recoverable corruption. Unreadable version headers are preserved
        // for explicit recovery rather than overwritten during a new scan.
        foreach (string suffix in new[] { "", ".bak" }) {
            try {
                await DurableJsonFile.ReadPrimaryAsync(path + suffix, WebAutomationJsonContext.Default.WebJournalVersion,
                    version => { RequireJournalVersion(version.Version, journal); return true; });
            }
            catch (JsonException ex) {
                throw new InvalidDataException("The journal version header is unreadable; the files were preserved.", ex);
            }
        }
    }
    static bool ValidateResults(WebResultsEnvelope value) {
        RequireJournalVersion(value.Version);
        return value.Rows is not null && value.Rows.Count <= 1_000_000 && value.Rows.All(r => r is not null && r.Item is not null &&
            Path.IsPathFullyQualified(r.Item.Path) && r.Item.GroupId != Guid.Empty &&
            (r.Snapshot is null || PathComparer.ForCurrentPlatform.Equals(r.Snapshot.Path, r.Item.Path))) &&
        value.Rows.Select(r => r.Item.Path).Distinct(PathComparer.ForCurrentPlatform).Count() == value.Rows.Count;
    }

    public async Task InitializeWebAutomationAsync() {
        ReserveAutomationWork();
        try {
            ClearSelectionPlan();
            string folder = ConfiguredStateFolder();
            var config = await LoadAutomationCoreAsync(folder);
            await new CleanupProtectionGuard(folder).RecoverAsync();
            ScheduleState = await ReconcileScheduledRunAtStartupAsync(folder);
            // A fresh process can report the default folder before its global
            // database wrapper has ever been loaded. Path equality alone cannot
            // authorize saving restored results back into that database.
            if (!await LoadDatabaseWithResultsAuthorityAsync(folder))
                throw new IOException("The saved results database could not be loaded. Restore it before using saved results.");
            string resultsPath = Path.Combine(folder, "WebResults.json");
            await RequireSupportedJournalCopiesAsync(resultsPath);
            var saved = await DurableJsonFile.ReadPrimaryAsync(resultsPath,
                WebAutomationJsonContext.Default.WebResultsEnvelope, ValidateResults);
            if (saved is not null) {
                _engine.Duplicates = saved.Rows.Select(r => r.Item).ToHashSet();
                _resultSnapshots = saved.Rows.Where(r => r.Snapshot is not null).ToDictionary(r => r.Item, r => r.Snapshot!);
                _resultStateFolder = folder;
                State = saved.Completed == true || saved.Completed is null && saved.Rows.Count > 0 ? ScanState.Done : ScanState.Idle;
                if (await SelectionPlanStore.LoadAsync(Path.Combine(folder, "SelectionPlan.json")) is not null) {
                    // An explanation saved before a later manual review is not
                    // current permission or a current checkbox selection.
                    config.Selection.HighlightedGroupsOnly = false;
                    await PreviewSelectionCoreAsync(config);
                }
            }
        }
        finally { EndDataWork(); }
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(IncludeFields = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ReviewRecord))]
internal partial class ReviewTokenJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
