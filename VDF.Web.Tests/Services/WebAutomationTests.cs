// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using VDF.Core;
using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Scheduling;
using VDF.Core.Selection;
using VDF.Core.Utils;
using VDF.Core.ViewModels;
using VDF.Web.Services;

namespace VDF.Web.Tests.Services;

[Collection("WebSettingsOverride")]
public sealed class WebAutomationTests : IAsyncLifetime {
    readonly string folder = Path.Combine(Path.GetTempPath(), "VDF.WebAutomation." + Guid.NewGuid().ToString("N"));
    readonly ScanService scan;
    readonly Guid group = Guid.NewGuid();
    readonly WebAutomationStore configStore;
    public WebAutomationTests() {
        Directory.CreateDirectory(folder);
        WebSettingsService.TestOverrideSettingsPath = Path.Combine(folder, "web-settings.json");
        scan = NewService();
        configStore = new WebAutomationStore(Path.Combine(folder, "web-settings.automation.json"));
    }
    ScanService NewService() {
        var result = new ScanService(new WebSettingsService());
        result.Settings.CustomDatabaseFolder = folder;
        result.Settings.IncludeList.Add(folder);
        return result;
    }
    public async Task InitializeAsync() {
        await ScanEngine.LoadDatabase(folder);
        DatabaseUtils.Database.Clear();
        await scan.LoadAutomationAsync();
        Assert.True(scan.SaveSettings());
    }
    public Task DisposeAsync() {
        scan.Dispose();
        WebSettingsService.TestOverrideSettingsPath = null;
        Directory.Delete(folder, true);
        return Task.CompletedTask;
    }
    DuplicateItem Seed(string name, DateTime? date = null) {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        if (date is { } time) File.SetLastWriteTimeUtc(path, time);
        var entry = new FileEntry(path);
        var item = new DuplicateItem(entry, .01f, group, DuplicateFlags.None);
        scan.Engine.Duplicates.Add(item);
        DatabaseUtils.Database.Add(entry);
        return item;
    }
    void Anchor() => scan.CaptureCleanupSnapshots(folder);

    [Fact]
    public async Task WebNewestAndOldestUseFullPrecisionAndStableTies() {
        var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var older = Seed("a.mp4", date);
        var newer = Seed("b.mp4", date.AddTicks(1));
        Anchor();
        Assert.True(newer.DateModified > older.DateModified);
        var latest = await scan.PreviewSelectionAsync(SelectionProfile.NewestFirst);
        Assert.Equal(newer.Path, Assert.Single(latest.Decisions, d => d.IsKeeper).Path);
        var oldest = await scan.PreviewSelectionAsync(SelectionProfile.OldestFirst);
        Assert.Equal(older.Path, Assert.Single(oldest.Decisions, d => d.IsKeeper).Path);
        Assert.Contains("Modification date", oldest.Decisions[0].Reason);
        Assert.Equal(2, scan.Duplicates.Count);
        Assert.True(File.Exists(older.Path) && File.Exists(newer.Path));
        Assert.Empty((await Reviews()).Records);
    }

    async Task<ReviewStore> Reviews() {
        var store = new ReviewStore(Path.Combine(folder, "ReviewedFiles.json"));
        await store.LoadAsync();
        return store;
    }

    [Fact]
    public async Task LocalNfoAndProtectedFolderApplyToPreviewAndCleanup() {
        var a = Seed("a.mp4"); var b = Seed("b.mp4"); Anchor();
        await File.WriteAllTextAsync(Path.Combine(folder, "a.nfo"), "<movie><favorite>true</favorite></movie>");
        var config = await scan.LoadAutomationAsync();
        config.Nfo.Enabled = true;
        await scan.SaveAutomationAsync(config);
        var plan = await scan.PreviewSelectionAsync();
        Assert.True(plan.Decisions.Single(d => d.Path == a.Path).IsProtected);
        var result = await scan.DeleteItemsAsync([a], true);
        Assert.Equal(0, result.Done); Assert.True(File.Exists(a.Path));
        config = await scan.LoadAutomationAsync();
        config.Selection.FolderRules.Add(new() { Directory = folder, ProtectAll = true });
        await scan.SaveAutomationAsync(config);
        plan = await scan.PreviewSelectionAsync();
        Assert.All(plan.Decisions, d => Assert.True(d.IsProtected));
        Assert.True(File.Exists(b.Path));
    }

    [Fact]
    public async Task ForeignProtectionWinsStaleExplicitReviewAndCanBeDeliberatelyClearedAfterReload() {
        var a = Seed("a.mp4"); Seed("b.mp4"); Anchor();
        var observed = (await scan.GetReviewRowsAsync()).Single(r => r.Item == a);
        scan.BeforeReviewCommitAsync = async () => {
            var current = await Reviews();
            await current.UpdateAsync(records => records.Append(new() { Identity = FileIdentity.Capture(a.Path)!,
                Choice = ReviewChoice.Keep, ManuallyProtected = true, Reason = "Other session" }));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => scan.SetReviewAsync(a, ReviewChoice.Remove, null, observed.Revision));
        var retained = Assert.Single((await Reviews()).Records);
        Assert.True(retained.ManuallyProtected); Assert.Equal(ReviewChoice.Keep, retained.Choice);
        scan.BeforeReviewCommitAsync = null;
        var refreshed = (await scan.GetReviewRowsAsync()).Single(r => r.Item == a);
        await scan.SetReviewAsync(a, ReviewChoice.Remove, null, refreshed.Revision);
        Assert.True(Assert.Single((await Reviews()).Records).ManuallyProtected);
        refreshed = (await scan.GetReviewRowsAsync()).Single(r => r.Item == a);
        await scan.SetReviewAsync(a, ReviewChoice.Remove, false, refreshed.Revision);
        Assert.False(Assert.Single((await Reviews()).Records).ManuallyProtected);
        var plan = await scan.PreviewSelectionAsync();
        Assert.True(plan.Decisions.Single(d => d.Path == a.Path).RemoveCandidate);
    }

    [Theory]
    [InlineData(ReviewChoice.Remove, null, false)]
    [InlineData(ReviewChoice.None, null, false)]
    [InlineData(ReviewChoice.Keep, true, true)]
    public async Task FreshReviewOfReplacementDoesNotInheritPreviousFilesProtection(ReviewChoice choice, bool? protect, bool expectedProtection) {
        var previous = Seed("a.mp4"); Seed("b.mp4"); Anchor();
        var row = (await scan.GetReviewRowsAsync()).Single(r => r.Item == previous);
        await scan.SetReviewAsync(previous, ReviewChoice.Keep, true, row.Revision);
        var previousIdentity = Assert.Single((await Reviews()).Records).Identity;
        File.Move(previous.Path, previous.Path + ".old");
        scan.Engine.Duplicates.Remove(previous);
        DatabaseUtils.Database.RemoveWhere(e => e.Path == previous.Path);
        var replacement = Seed("a.mp4"); Anchor();
        Assert.False(previousIdentity.Matches(FileIdentity.Capture(replacement.Path)));
        row = (await scan.GetReviewRowsAsync()).Single(r => r.Item == replacement);
        Assert.False(row.ManuallyProtected);
        Assert.Equal(ReviewChoice.None, row.Choice);
        await scan.SetReviewAsync(replacement, choice, protect, row.Revision);
        var saved = Assert.Single((await Reviews()).Records);
        Assert.True(saved.Identity.Matches(FileIdentity.Capture(replacement.Path)));
        Assert.Equal(choice, saved.Choice);
        Assert.Equal(expectedProtection, saved.ManuallyProtected);
    }

    [Fact]
    public async Task PreviewNeverAutosavesOverForeignManualGroupVeto() {
        var a = Seed("a.mp4"); var b = Seed("b.mp4"); Anchor();
        var row = (await scan.GetReviewRowsAsync()).Single(r => r.Item == b);
        await scan.SetReviewAsync(b, ReviewChoice.Ignore, null, row.Revision);
        var before = await File.ReadAllBytesAsync(Path.Combine(folder, "ReviewedFiles.json"));
        var plan = await scan.PreviewSelectionAsync();
        Assert.All(plan.Decisions, d => Assert.False(d.RemoveCandidate));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(folder, "ReviewedFiles.json")));
        var result = await scan.DeleteItemsAsync([a], true);
        Assert.Equal(0, result.Done); Assert.True(File.Exists(a.Path));
    }

    [Fact]
    public async Task ConcurrentConfigurationSaveIsRejectedWithoutErasingProtection() {
        var stale = await configStore.LoadAsync();
        var newer = await configStore.LoadAsync();
        newer.Nfo.Enabled = true;
        await configStore.SaveAsync(newer, folder);
        stale.Selection.AutoAfterScan = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scan.SaveAutomationAsync(stale));
        Assert.True((await configStore.LoadAsync()).Nfo.Enabled);
        Assert.False((await configStore.LoadAsync()).Selection.AutoAfterScan);
        Assert.True((await CleanupProtectionPolicyStore.LoadAsync(folder)).NfoOptions.Enabled);
    }

    [Fact]
    public async Task HandEditedProtectionWithoutChangedGenerationStillRejectsStaleSave() {
        var stale = await configStore.LoadAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(configStore.Path))!;
        json["nfo"]!["enabled"] = true;
        await File.WriteAllTextAsync(configStore.Path, json.ToJsonString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => scan.SaveAutomationAsync(stale));
        Assert.True((await configStore.LoadAsync()).Nfo.Enabled);
    }

    [Fact]
    public async Task UnavailableExplicitStateDoesNotBorrowDefaultState() {
        scan.Settings.CustomDatabaseFolder = Path.Combine(folder, "unavailable");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => scan.LoadAutomationAsync());
        Assert.False(Directory.Exists(scan.Settings.CustomDatabaseFolder));
    }

    [Theory]
    [InlineData("nfo", "enabled")]
    [InlineData("selection", "overlapPolicy")]
    public async Task TruncatedProtectionDocumentCannotReanchorDefaults(string section, string property) {
        var json = JsonNode.Parse(await File.ReadAllTextAsync(configStore.Path))!;
        json[section]!.AsObject().Remove(property);
        await File.WriteAllTextAsync(configStore.Path, json.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => scan.LoadAutomationAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => CleanupProtectionPolicyStore.LoadAsync(folder));
    }

    [Fact]
    public async Task QuarantineRequiresUnchangedPreviewLeavesSidecarsAndRestoreNeverOverwrites() {
        var a = Seed("a.mp4"); Seed("b.mp4"); Anchor();
        string sidecar = Path.Combine(folder, "a.nfo");
        await File.WriteAllTextAsync(sidecar, "<movie/>");
        var config = await scan.LoadAutomationAsync();
        config.QuarantineDirectory = Path.Combine(folder, "quarantine");
        await scan.SaveAutomationAsync(config);
        var denied = await scan.QuarantineItemsAsync([a]);
        Assert.Equal(0, denied.Done); Assert.True(File.Exists(a.Path));
        var preview = Assert.Single(await scan.PreviewQuarantineAsync([a]));
        Assert.Contains(sidecar, preview.SidecarsLeftInPlace);
        config = await scan.LoadAutomationAsync();
        config.QuarantineDirectory = Path.Combine(folder, "changed-destination");
        await scan.SaveAutomationAsync(config);
        denied = await scan.QuarantineItemsAsync([a]);
        Assert.Equal(0, denied.Done); Assert.True(File.Exists(a.Path));
        await scan.PreviewQuarantineAsync([a]);
        var done = await scan.QuarantineItemsAsync([a]);
        Assert.Equal(1, done.Done); Assert.False(File.Exists(a.Path)); Assert.True(File.Exists(sidecar));
        var operation = Assert.Single(await scan.GetQuarantineHistoryAsync());
        await File.WriteAllTextAsync(a.Path, "new media");
        await Assert.ThrowsAsync<IOException>(() => scan.RestoreQuarantineAsync(operation.OperationId));
        Assert.Equal("new media", await File.ReadAllTextAsync(a.Path));
        File.Delete(a.Path);
        await scan.RestoreQuarantineAsync(operation.OperationId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(a.Path));
        Assert.True(File.Exists(sidecar));
    }

    [Fact]
    public async Task SchedulerDurablyClaimsBeforeSearchAndDoesNotRepeatAfterRestartWithoutBrowser() {
        var config = await scan.LoadAutomationAsync();
        config.Schedule = new() { Enabled = true, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        config.Selection.AutoAfterScan = true;
        await scan.SaveAutomationAsync(config);
        var now = new DateTimeOffset(2026, 1, 1, 12, 1, 0, TimeSpan.Zero);
        int searches = 0;
        scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
        scan.StartSearchOverride = () => {
            searches++;
            Assert.NotNull(scan.ScheduleState?.LastClaimedSlotUtc);
            Assert.True(File.Exists(Path.Combine(folder, "WebSchedule.json")));
            Seed("a.mp4"); Seed("b.mp4");
            ScanEngine.SaveDatabase();
        };
        await scan.CheckScheduleAsync(now);
        Assert.Equal(1, searches); Assert.True(scan.IsBusy);
        await scan.CompleteWebScanAsync(false);
        Assert.Equal("Completed", scan.ScheduleState?.Status);
        Assert.NotNull(scan.LastSelectionPlan);
        Assert.True(File.Exists(Path.Combine(folder, "WebResults.json")));
        Assert.Empty((await Reviews()).Records);
        using var restarted = NewService();
        await restarted.InitializeWebAutomationAsync();
        Assert.Equal(2, restarted.Duplicates.Count);
        Assert.Equal(2, DatabaseUtils.Database.Count);
        Assert.NotNull(restarted.LastSelectionPlan);
        restarted.StartSearchOverride = () => searches++;
        await restarted.CheckScheduleAsync(now.AddMinutes(1));
        Assert.Equal(1, searches); Assert.False(restarted.IsBusy);
        Assert.All(Directory.GetFiles(folder, "*.mp4"), p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public async Task MissingEstablishedClaimJournalRefusesScheduledWork() {
        var config = await scan.LoadAutomationAsync();
        config.Schedule = new() { Enabled = true, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        await scan.SaveAutomationAsync(config);
        scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
        int started = 0;
        scan.StartSearchOverride = () => started++;
        var now = DateTimeOffset.UtcNow;
        await scan.CheckScheduleAsync(now);
        await scan.CompleteWebScanAsync(true);
        File.Delete(Path.Combine(folder, "WebSchedule.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => scan.CheckScheduleAsync(now.AddMinutes(6)));
        Assert.Equal(1, started);
    }

    [Theory]
    [InlineData(false, "prepare")]
    [InlineData(true, "prepare")]
    [InlineData(false, "abort")]
    [InlineData(true, "abort")]
    [InlineData(false, "interrupted")]
    [InlineData(true, "interrupted")]
    public async Task ReplacingScanDurablyDiscardsPreviousResultsBeforePreparation(bool scheduled, string outcome) {
        Seed("previous-a.mp4"); Seed("previous-b.mp4");
        var previousPaths = scan.Duplicates.Select(item => item.Path).ToArray();
        ScanEngine.SaveDatabase();
        await scan.CompleteWebScanAsync(false);
        await scan.RemoveFromResultsAsync([]);
        Assert.Equal(2, scan.Duplicates.Count);
        var config = await scan.LoadAutomationAsync();
        config.Schedule = new() { Enabled = scheduled, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        await scan.SaveAutomationAsync(config);
        int prepared = 0, started = 0;
        scan.PrepareScanAsync = (_, _) => {
            prepared++;
            AssertEmptySavedResults(folder);
            return outcome == "prepare" ? Task.FromException(new IOException("Synthetic preparation failure")) : Task.CompletedTask;
        };
        scan.StartSearchOverride = () => {
            started++;
            AssertEmptySavedResults(folder);
            DatabaseUtils.Database.Clear();
            ScanEngine.SaveDatabase();
        };
        try {
            if (scheduled) await scan.CheckScheduleAsync(DateTimeOffset.UtcNow);
            else await scan.StartScanAndCompare();
            Assert.Equal(1, prepared);
            Assert.Equal(outcome == "prepare" ? 0 : 1, started);
            if (outcome == "abort") await scan.CompleteWebScanAsync(true);
            Assert.Empty(scan.Duplicates);
            AssertEmptySavedResults(folder);
            using var restarted = NewService();
            await restarted.InitializeWebAutomationAsync();
            Assert.Empty(restarted.Duplicates);
            Assert.Equal(ScanState.Idle, restarted.State);
            Assert.All(previousPaths, path => Assert.True(File.Exists(path)));
        }
        finally {
            if (scan.IsBusy) await scan.CompleteWebScanAsync(true);
        }
    }

    static void AssertEmptySavedResults(string stateFolder) {
        foreach (var suffix in new[] { "", ".bak" }) {
            var value = JsonNode.Parse(File.ReadAllText(Path.Combine(stateFolder, "WebResults.json" + suffix)))!;
            Assert.Empty(value["rows"]!.AsArray());
        }
    }

    Action RemoveAutomationConfigCopies() {
        string path = configStore.Path;
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        File.Delete(path); File.Delete(path + ".bak");
        return () => { File.WriteAllBytes(path, primary); File.WriteAllBytes(path + ".bak", backup); };
    }

    [Theory]
    [InlineData(false, false, "complete")]
    [InlineData(true, false, "complete")]
    [InlineData(false, true, "complete")]
    [InlineData(true, true, "complete")]
    [InlineData(false, false, "prepare")]
    [InlineData(true, false, "prepare")]
    [InlineData(false, true, "prepare")]
    [InlineData(true, true, "prepare")]
    [InlineData(false, false, "abort")]
    [InlineData(true, false, "abort")]
    [InlineData(false, true, "abort")]
    [InlineData(true, true, "abort")]
    [InlineData(false, false, "interrupted")]
    [InlineData(true, false, "interrupted")]
    [InlineData(false, true, "interrupted")]
    [InlineData(true, true, "interrupted")]
    public async Task MissingAutomationConfigCannotReviveReplacedResults(bool freshService, bool switchFolder, string outcome) {
        Seed("previous-a.mp4"); Seed("previous-b.mp4"); Anchor();
        ScanEngine.SaveDatabase();
        await scan.RemoveFromResultsAsync([]);
        string target = switchFolder ? Path.Combine(folder, "next-state") : folder;
        if (switchFolder) {
            Directory.CreateDirectory(target);
            foreach (string name in new[] { "WebResults.json", "WebResults.json.bak", "ScannedFiles.db" })
                File.Copy(Path.Combine(folder, name), Path.Combine(target, name));
        }
        var actor = freshService ? NewService() : scan;
        actor.Settings.CustomDatabaseFolder = target;
        var restore = RemoveAutomationConfigCopies();
        int prepared = 0, started = 0;
        actor.PrepareScanAsync = async (_, _) => {
            prepared++;
            AssertEmptySavedResults(target);
            if (!freshService) AssertEmptySavedResults(folder);
            if (outcome == "prepare") throw new IOException("Synthetic preparation failure");
            Assert.True(await ScanEngine.LoadDatabase(target));
        };
        actor.StartSearchOverride = () => {
            started++;
            foreach (string name in new[] { "replacement-a.mp4", "replacement-b.mp4" }) {
                string path = Path.Combine(folder, name);
                File.WriteAllBytes(path, [1, 2, 3, 4]);
                var entry = new FileEntry(path);
                actor.Engine.Duplicates.Add(new DuplicateItem(entry, .01f, group, DuplicateFlags.None));
                DatabaseUtils.Database.Add(entry);
            }
            ScanEngine.SaveDatabase();
        };
        try {
            await actor.StartScanAndCompare();
            Assert.Equal(1, prepared);
            Assert.Equal(outcome == "prepare" ? 0 : 1, started);
            if (outcome == "complete") await actor.CompleteWebScanAsync(false);
            if (outcome == "abort") await actor.CompleteWebScanAsync(true);
            if (outcome == "prepare") Assert.Equal(ScanState.Error, actor.State);
            if (outcome == "complete") Assert.Equal(ScanState.Done, actor.State);
            if (outcome != "complete") AssertEmptySavedResults(target);
            // Missing established protection settings must still be restored;
            // result persistence must not recreate permissive defaults.
            Assert.False(File.Exists(configStore.Path));
            restore();
            using var restarted = NewService();
            restarted.Settings.CustomDatabaseFolder = target;
            await restarted.InitializeWebAutomationAsync();
            Assert.Equal(outcome == "complete" ? ScanState.Done : ScanState.Idle, restarted.State);
            Assert.Equal(outcome == "complete" ? 2 : 0, restarted.Duplicates.Count);
            Assert.All(restarted.Duplicates, item => Assert.Contains("replacement-", item.Path));
            if (switchFolder && !freshService) AssertEmptySavedResults(folder);
            Assert.Equal(outcome == "prepare" ? 2 : 4, Directory.GetFiles(folder, "*.mp4", SearchOption.AllDirectories).Length);
            Assert.All(Directory.GetFiles(folder, "*.mp4", SearchOption.AllDirectories),
                path => Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path)));
        }
        finally {
            restore();
            if (actor.IsBusy) await actor.CompleteWebScanAsync(true);
            if (freshService) actor.Dispose();
        }
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("remove")]
    [InlineData("clear")]
    public async Task MissingAutomationConfigCannotReviveExplicitlyClearedResults(string action) {
        var a = Seed("previous-a.mp4"); Seed("previous-b.mp4"); Anchor();
        ScanEngine.SaveDatabase();
        await scan.RemoveFromResultsAsync([]);
        var restore = RemoveAutomationConfigCopies();
        try {
            if (action == "reset") await scan.ResetAsync();
            else if (action == "remove") await scan.RemoveFromResultsAsync([a]);
            else await scan.ClearDatabaseAsync();
            AssertEmptySavedResults(folder);
            Assert.False(File.Exists(configStore.Path));
            restore();
            using var restarted = NewService();
            await restarted.InitializeWebAutomationAsync();
            Assert.Empty(restarted.Duplicates);
            Assert.Equal(action == "remove" ? ScanState.Done : ScanState.Idle, restarted.State);
            Assert.Equal(2, Directory.GetFiles(folder, "*.mp4").Length);
            Assert.All(Directory.GetFiles(folder, "*.mp4"), path => Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path)));
        }
        finally { restore(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MissingAutomationConfigStillRefusesUnwritableResultGeneration(bool freshService, bool corrupt) {
        Seed("retained-a.mp4"); Seed("retained-b.mp4"); Anchor();
        ScanEngine.SaveDatabase();
        await scan.RemoveFromResultsAsync([]);
        var restore = RemoveAutomationConfigCopies();
        string path = Path.Combine(folder, "WebResults.json");
        if (corrupt) { File.WriteAllText(path, "{"); File.WriteAllText(path + ".bak", "{"); }
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        using var lease = corrupt ? null : JournalWriteLease.Acquire(path);
        var actor = freshService ? NewService() : scan;
        int prepared = 0, started = 0;
        actor.PrepareScanAsync = (_, _) => { prepared++; return Task.CompletedTask; };
        actor.StartSearchOverride = () => started++;
        try {
            await actor.StartScanAndCompare();
            Assert.Equal(ScanState.Error, actor.State);
            Assert.False(actor.IsBusy);
            Assert.Equal(0, prepared); Assert.Equal(0, started);
            Assert.Equal(freshService ? 0 : 2, actor.Duplicates.Count);
            Assert.Equal(primary, File.ReadAllBytes(path));
            Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
            Assert.False(File.Exists(configStore.Path));
        }
        finally { restore(); if (freshService) actor.Dispose(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MissingAutomationConfigPersistsCurrentResultsWithOnlyBackupOrInMemoryHint(bool freshService, bool retainBackup) {
        Seed("previous-a.mp4"); Seed("previous-b.mp4"); Anchor();
        ScanEngine.SaveDatabase();
        await scan.RemoveFromResultsAsync([]);
        File.Delete(Path.Combine(folder, "WebResults.json"));
        if (!retainBackup) File.Delete(Path.Combine(folder, "WebResults.json.bak"));
        var restore = RemoveAutomationConfigCopies();
        var actor = freshService ? NewService() : scan;
        actor.PrepareScanAsync = (_, _) => Task.CompletedTask;
        actor.StartSearchOverride = () => {
            foreach (string name in new[] { "current-a.mp4", "current-b.mp4" }) {
                string path = Path.Combine(folder, name);
                File.WriteAllBytes(path, [1, 2, 3, 4]);
                var entry = new FileEntry(path);
                actor.Engine.Duplicates.Add(new DuplicateItem(entry, .01f, group, DuplicateFlags.None));
                DatabaseUtils.Database.Add(entry);
            }
            ScanEngine.SaveDatabase();
        };
        try {
            await actor.StartScanAndCompare();
            await actor.CompleteWebScanAsync(false);
            Assert.Equal(ScanState.Done, actor.State);
            Assert.False(File.Exists(configStore.Path));
            restore();
            using var restarted = NewService();
            await restarted.InitializeWebAutomationAsync();
            Assert.Equal(2, restarted.Duplicates.Count);
            Assert.All(restarted.Duplicates, item => Assert.Contains("current-", item.Path));
            Assert.Equal(4, Directory.GetFiles(folder, "*.mp4").Length);
        }
        finally { restore(); if (actor.IsBusy) await actor.CompleteWebScanAsync(true); if (freshService) actor.Dispose(); }
    }

    [Theory]
    [InlineData(false, false, "both-missing")]
    [InlineData(true, false, "both-missing")]
    [InlineData(false, true, "both-missing")]
    [InlineData(true, true, "both-missing")]
    [InlineData(false, false, "primary-missing")]
    [InlineData(true, false, "primary-missing")]
    [InlineData(false, true, "primary-missing")]
    [InlineData(true, true, "primary-missing")]
    [InlineData(false, false, "corrupt")]
    [InlineData(true, false, "corrupt")]
    [InlineData(false, true, "corrupt")]
    [InlineData(true, true, "corrupt")]
    [InlineData(false, false, "future")]
    [InlineData(true, false, "future")]
    [InlineData(false, true, "future")]
    [InlineData(true, true, "future")]
    public async Task AutomationConfigFaultDuringCompletionPreservesResultsAndRequiresSettingsRestoration(bool scheduled, bool autoPreview, string fault) {
        var config = await scan.LoadAutomationAsync();
        config.Schedule = new() { Enabled = scheduled, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        config.Selection.AutoAfterScan = autoPreview;
        await scan.SaveAutomationAsync(config);
        scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
        int starts = 0;
        scan.StartSearchOverride = () => { starts++; Seed("completed-a.mp4"); Seed("completed-b.mp4"); ScanEngine.SaveDatabase(); };
        var now = DateTimeOffset.UtcNow;
        if (scheduled) await scan.CheckScheduleAsync(now);
        else await scan.StartScanAndCompare();
        Assert.True(scan.IsBusy);
        Assert.Equal(1, starts);
        byte[] original = File.ReadAllBytes(configStore.Path);
        var restore = RemoveAutomationConfigCopies();
        if (fault == "primary-missing") File.WriteAllBytes(configStore.Path + ".bak", original);
        if (fault == "corrupt") { File.WriteAllText(configStore.Path, "{"); File.WriteAllText(configStore.Path + ".bak", "{"); }
        if (fault == "future") {
            var future = JsonNode.Parse(original)!; future["version"] = 2;
            File.WriteAllText(configStore.Path, future.ToJsonString());
            File.WriteAllBytes(configStore.Path + ".bak", original);
        }
        byte[]? faultPrimary = File.Exists(configStore.Path) ? File.ReadAllBytes(configStore.Path) : null;
        byte[]? faultBackup = File.Exists(configStore.Path + ".bak") ? File.ReadAllBytes(configStore.Path + ".bak") : null;
        try {
            await scan.CompleteWebScanAsync(false);
            Assert.Equal(ScanState.Error, scan.State);
            Assert.NotNull(scan.ErrorMessage);
            Assert.False(scan.IsBusy);
            Assert.Equal(2, scan.Duplicates.Count);
            foreach (string suffix in new[] { "", ".bak" }) {
                var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "WebResults.json" + suffix)))!;
                Assert.Equal(2, saved["rows"]!.AsArray().Count);
                Assert.True(saved["completed"]!.GetValue<bool>());
            }
            Assert.Equal(faultPrimary is not null, File.Exists(configStore.Path));
            Assert.Equal(faultBackup is not null, File.Exists(configStore.Path + ".bak"));
            if (faultPrimary is not null) Assert.Equal(faultPrimary, File.ReadAllBytes(configStore.Path));
            if (faultBackup is not null) Assert.Equal(faultBackup, File.ReadAllBytes(configStore.Path + ".bak"));
            restore();
            using var restarted = NewService();
            await restarted.InitializeWebAutomationAsync();
            Assert.Equal(ScanState.Done, restarted.State);
            Assert.Equal(2, restarted.Duplicates.Count);
            Assert.All(restarted.Duplicates, item => {
                Assert.Contains("completed-", item.Path);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(item.Path));
            });
            if (scheduled) {
                Assert.StartsWith("Failed:", restarted.ScheduleState?.Status);
                await restarted.CheckScheduleAsync(now);
                Assert.False(restarted.IsBusy);
                using var lease = JournalWriteLease.Acquire(Path.Combine(folder, ScanService.ScheduledRunLeaseFileName));
            }
        }
        finally { restore(); if (scan.IsBusy) await scan.CompleteWebScanAsync(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedResultsCompleteInMemoryAndDiscloseRestartLimit(bool scheduled) {
        var config = await scan.LoadAutomationAsync();
        config.Schedule = new() { Enabled = scheduled, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        config.Selection.AutoAfterScan = true;
        await scan.SaveAutomationAsync(config);
        scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
        scan.StartSearchOverride = () => {
            // Two references serialize as more than the real 32 MiB limit.
            string largeFormat = new('x', 17 * 1024 * 1024);
            Seed("large-a.mp4").Format = largeFormat;
            Seed("large-b.mp4").Format = largeFormat;
            ScanEngine.SaveDatabase();
        };
        if (scheduled) await scan.CheckScheduleAsync(DateTimeOffset.UtcNow);
        else await scan.StartScanAndCompare();
        Assert.True(scan.IsBusy);
        await scan.CompleteWebScanAsync(false);
        Assert.Equal(ScanState.Done, scan.State);
        Assert.False(scan.IsBusy);
        Assert.Null(scan.ErrorMessage);
        Assert.Equal(2, scan.Duplicates.Count);
        Assert.Equal(2, scan.LastSelectionPlan?.Decisions.Count);
        Assert.Contains("until the service restarts", scan.ResultsPersistenceWarning);
        Assert.Null(scan.SelectionPersistenceWarning);
        if (scheduled) Assert.Equal("Completed", scan.ScheduleState?.Status);
        AssertEmptySavedResults(folder);
        Assert.Empty(Directory.GetFiles(folder, "*.tmp-*"));
        using (var restarted = NewService()) {
            await restarted.InitializeWebAutomationAsync();
            Assert.Empty(restarted.Duplicates);
            Assert.Equal(2, restarted.DatabaseEntryCount);
        }
        Assert.All(scan.Duplicates, item => Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(item.Path)));
        // A new small result generation can be saved and clears the warning.
        scan.StartSearchOverride = () => { Seed("small-a.mp4"); Seed("small-b.mp4"); ScanEngine.SaveDatabase(); };
        await scan.StartScanAndCompare();
        await scan.CompleteWebScanAsync(false);
        Assert.Equal(ScanState.Done, scan.State);
        Assert.Null(scan.ResultsPersistenceWarning);
        using var smallRestart = NewService();
        await smallRestart.InitializeWebAutomationAsync();
        Assert.Equal(2, smallRestart.Duplicates.Count);
        Assert.All(smallRestart.Duplicates, item => Assert.Contains("small-", item.Path));
    }

    [Fact]
    public async Task OversizedSelectionPreviewReplacesStaleSavedCopiesAndRetainsCurrentPlan() {
        var a = Seed("preview-a.mp4"); Seed("preview-b.mp4"); Anchor();
        await scan.PreviewSelectionAsync();
        string largeReason = new('x', 17 * 1024 * 1024);
        var plan = new SelectionPlan { Decisions = [
            new() { Path = a.Path, Reason = largeReason }, new() { Path = a.Path, Reason = largeReason }] };
        await scan.SaveSelectionPlanForSessionAsync(folder, plan);
        Assert.Same(plan, scan.LastSelectionPlan);
        Assert.Contains("until the service restarts", scan.SelectionPersistenceWarning);
        foreach (string suffix in new[] { "", ".bak" }) {
            var saved = await SelectionPlanStore.LoadAsync(Path.Combine(folder, "SelectionPlan.json" + suffix));
            Assert.Empty(saved!.Decisions);
        }
        Assert.Empty(Directory.GetFiles(folder, "*.tmp-*"));
        await scan.PreviewSelectionAsync();
        Assert.Null(scan.SelectionPersistenceWarning);
        Assert.Equal(2, scan.LastSelectionPlan?.Decisions.Count);
        Assert.All(scan.Duplicates, item => Assert.True(File.Exists(item.Path)));
    }

    [Fact]
    public async Task LargeScheduledResultsAndGeneratedAutomaticPreviewBothComplete() {
        var config = await scan.LoadAutomationAsync();
        config.Schedule = new() { Enabled = true, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        config.Selection.AutoAfterScan = true;
        await scan.SaveAutomationAsync(config);
        scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
        scan.StartSearchOverride = () => {
            // Missing-media results still require explanations. Realistic long
            // nested paths exceed the actual journal cap without large media.
            string parent = folder;
            for (int i = 0; i < 18; i++) parent = Path.Combine(parent, new string('a', 100));
            for (int i = 0; i < 20_000; i++) scan.Engine.Duplicates.Add(new DuplicateItem {
                Path = Path.Combine(parent, $"missing-{i:D5}.mp4"), Folder = parent, GroupId = group });
            ScanEngine.SaveDatabase();
        };
        await scan.CheckScheduleAsync(DateTimeOffset.UtcNow);
        await scan.CompleteWebScanAsync(false);
        Assert.Equal(ScanState.Done, scan.State);
        Assert.Equal("Completed", scan.ScheduleState?.Status);
        Assert.Null(scan.ErrorMessage);
        Assert.Equal(20_000, scan.Duplicates.Count);
        Assert.Equal(20_000, scan.LastSelectionPlan?.Decisions.Count);
        Assert.All(scan.LastSelectionPlan!.Decisions, decision => {
            Assert.False(decision.RemoveCandidate);
            Assert.True(decision.RequiresReview);
        });
        Assert.NotNull(scan.ResultsPersistenceWarning);
        Assert.NotNull(scan.SelectionPersistenceWarning);
        AssertEmptySavedResults(folder);
        Assert.Empty((await SelectionPlanStore.LoadAsync(Path.Combine(folder, "SelectionPlan.json")))!.Decisions);
        Assert.Empty(Directory.GetFiles(folder, "*.mp4", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OversizedResultUpdateCannotRevivePreviousSavedRowsAndResetClearsWarning() {
        Seed("updated-a.mp4"); Seed("updated-b.mp4"); Anchor();
        ScanEngine.SaveDatabase();
        await scan.RemoveFromResultsAsync([]);
        Assert.NotEmpty(JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "WebResults.json")))!["rows"]!.AsArray());
        string largeFormat = new('x', 17 * 1024 * 1024);
        foreach (var item in scan.Duplicates) item.Format = largeFormat;
        await scan.RemoveFromResultsAsync([]);
        Assert.Equal(2, scan.Duplicates.Count);
        Assert.NotNull(scan.ResultsPersistenceWarning);
        AssertEmptySavedResults(folder);
        await scan.ResetAsync();
        Assert.Empty(scan.Duplicates);
        Assert.Null(scan.ResultsPersistenceWarning);
        AssertEmptySavedResults(folder);
        Assert.All(Directory.GetFiles(folder, "*.mp4"), path => Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonSizeResultsSaveFailureStillFailsCompletion(bool corrupt) {
        scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
        scan.StartSearchOverride = () => { Seed("failure-a.mp4"); Seed("failure-b.mp4"); };
        await scan.StartScanAndCompare();
        string path = Path.Combine(folder, "WebResults.json");
        if (corrupt) { File.WriteAllText(path, "{"); File.WriteAllText(path + ".bak", "{"); }
        using var lease = corrupt ? null : JournalWriteLease.Acquire(path);
        await scan.CompleteWebScanAsync(false);
        Assert.Equal(ScanState.Error, scan.State);
        Assert.NotNull(scan.ErrorMessage);
        Assert.Null(scan.ResultsPersistenceWarning);
        Assert.Equal(2, scan.Duplicates.Count);
        Assert.All(scan.Duplicates, item => Assert.True(File.Exists(item.Path)));
    }

    [Theory]
    [InlineData(false, false, "same")]
    [InlineData(true, false, "same")]
    [InlineData(false, true, "same")]
    [InlineData(true, true, "same")]
    [InlineData(false, false, "renamed")]
    [InlineData(true, false, "renamed")]
    [InlineData(false, true, "renamed")]
    [InlineData(true, true, "renamed")]
    [InlineData(false, false, "changed")]
    [InlineData(true, false, "changed")]
    [InlineData(false, true, "changed")]
    [InlineData(true, true, "changed")]
    public async Task FutureResultsSchemaIsPreservedEvenWithValidOlderCopy(bool duringCompletion, bool futureBackup, string shape) {
        Seed("future-a.mp4"); Seed("future-b.mp4"); Anchor();
        ScanEngine.SaveDatabase();
        await scan.RemoveFromResultsAsync([]);
        scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
        int started = 0;
        scan.StartSearchOverride = () => { started++; Seed("new-a.mp4"); Seed("new-b.mp4"); };
        if (duringCompletion) await scan.StartScanAndCompare();
        string path = Path.Combine(folder, "WebResults.json");
        WriteFutureResultsCopy(path, futureBackup, shape);
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        if (duringCompletion) await scan.CompleteWebScanAsync(false);
        else await scan.StartScanAndCompare();
        Assert.Equal(ScanState.Error, scan.State);
        Assert.Contains("unsupported", scan.ErrorMessage);
        Assert.Equal(duringCompletion ? 1 : 0, started);
        Assert.Equal(2, scan.Duplicates.Count);
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
        Assert.Null(scan.ResultsPersistenceWarning);
        Assert.All(scan.Duplicates, item => Assert.True(File.Exists(item.Path)));
    }

    static void WriteFutureResultsCopy(string path, bool backup, string shape) {
        var future = JsonNode.Parse(File.ReadAllText(path))!;
        future["version"] = 2;
        if (shape == "renamed") { future["results"] = future["rows"]!.DeepClone(); future.AsObject().Remove("rows"); }
        if (shape == "changed") future["rows"] = "a future payload format";
        File.WriteAllText(path + (backup ? ".bak" : ""), future.ToJsonString());
    }

    [Theory]
    [InlineData(false, "same")]
    [InlineData(true, "same")]
    [InlineData(false, "renamed")]
    [InlineData(true, "renamed")]
    [InlineData(false, "changed")]
    [InlineData(true, "changed")]
    public async Task FutureResultsCopiesRefuseInitialization(bool backup, string shape) {
        Seed("future-a.mp4"); Seed("future-b.mp4"); Anchor();
        ScanEngine.SaveDatabase();
        await scan.RemoveFromResultsAsync([]);
        string path = Path.Combine(folder, "WebResults.json");
        WriteFutureResultsCopy(path, backup, shape);
        byte[] primary = File.ReadAllBytes(path), previousBackup = File.ReadAllBytes(path + ".bak");
        using var restarted = NewService();
        await Assert.ThrowsAsync<NotSupportedException>(() => restarted.InitializeWebAutomationAsync());
        Assert.Empty(restarted.Duplicates);
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(previousBackup, File.ReadAllBytes(path + ".bak"));
    }

    [Theory]
    [InlineData(false, "same")]
    [InlineData(true, "same")]
    [InlineData(false, "renamed")]
    [InlineData(true, "renamed")]
    [InlineData(false, "changed")]
    [InlineData(true, "changed")]
    public async Task FuturePreviewCopyIsPreservedBeforeVersionOnePayloadParsing(bool backup, string shape) {
        Seed("future-a.mp4"); Seed("future-b.mp4"); Anchor();
        await scan.PreviewSelectionAsync();
        string path = Path.Combine(folder, "SelectionPlan.json");
        var future = JsonNode.Parse(File.ReadAllText(path))!;
        future["version"] = 2;
        if (shape == "renamed") { future["preview"] = future["plan"]!.DeepClone(); future.AsObject().Remove("plan"); }
        if (shape == "changed") future["plan"] = "a future payload format";
        File.WriteAllText(path + (backup ? ".bak" : ""), future.ToJsonString());
        byte[] primary = File.ReadAllBytes(path), previousBackup = File.ReadAllBytes(path + ".bak");
        await Assert.ThrowsAsync<NotSupportedException>(() => scan.PreviewSelectionAsync());
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(previousBackup, File.ReadAllBytes(path + ".bak"));
        Assert.Null(scan.SelectionPersistenceWarning);
        Assert.All(scan.Duplicates, item => Assert.True(File.Exists(item.Path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedEmptyScanRestoresAsDone(bool scheduled) {
        var config = await scan.LoadAutomationAsync();
        config.Schedule = new() { Enabled = scheduled, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        await scan.SaveAutomationAsync(config);
        scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
        scan.StartSearchOverride = () => ScanEngine.SaveDatabase();
        if (scheduled) await scan.CheckScheduleAsync(DateTimeOffset.UtcNow);
        else await scan.StartScanAndCompare();
        await scan.CompleteWebScanAsync(false);
        Assert.Equal(ScanState.Done, scan.State);
        using var restarted = NewService();
        await restarted.InitializeWebAutomationAsync();
        Assert.Equal(ScanState.Done, restarted.State);
        Assert.Empty(restarted.Duplicates);
        foreach (string suffix in new[] { "", ".bak" })
            Assert.True(JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "WebResults.json" + suffix)))!["completed"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyResultsRestoreConservativelyWithoutCompletionMetadata(bool populated) {
        Seed("legacy-a.mp4"); Seed("legacy-b.mp4"); Anchor();
        ScanEngine.SaveDatabase();
        await scan.RemoveFromResultsAsync([]);
        foreach (string suffix in new[] { "", ".bak" }) {
            string path = Path.Combine(folder, "WebResults.json" + suffix);
            var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            saved.Remove("completed");
            if (!populated) saved["rows"] = new JsonArray();
            File.WriteAllText(path, saved.ToJsonString());
        }
        using var restarted = NewService();
        await restarted.InitializeWebAutomationAsync();
        Assert.Equal(populated ? ScanState.Done : ScanState.Idle, restarted.State);
        Assert.Equal(populated ? 2 : 0, restarted.Duplicates.Count);
    }

    [Fact]
    public async Task CorruptSelectionPreviewIsPreservedAndStillFails() {
        Seed("preview-a.mp4"); Seed("preview-b.mp4"); Anchor();
        string path = Path.Combine(folder, "SelectionPlan.json");
        File.WriteAllText(path, "{"); File.WriteAllText(path + ".bak", "{");
        await Assert.ThrowsAsync<InvalidDataException>(() => scan.PreviewSelectionAsync());
        Assert.Null(scan.SelectionPersistenceWarning);
        Assert.Equal("{", File.ReadAllText(path));
        Assert.Equal("{", File.ReadAllText(path + ".bak"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnwritableResultsSnapshotRefusesScanBeforeDiscardingVisibleResults(bool corrupt) {
        Seed("preserved-a.mp4"); Seed("preserved-b.mp4");
        ScanEngine.SaveDatabase();
        await scan.CompleteWebScanAsync(false);
        await scan.RemoveFromResultsAsync([]);
        string path = Path.Combine(folder, "WebResults.json");
        if (corrupt) {
            File.WriteAllText(path, "{");
            File.WriteAllText(path + ".bak", "{");
        }
        byte[] before = File.ReadAllBytes(path);
        using var lease = corrupt ? null : new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        int prepared = 0, started = 0;
        scan.PrepareScanAsync = (_, _) => { prepared++; return Task.CompletedTask; };
        scan.StartSearchOverride = () => started++;
        try {
            await scan.StartScanAndCompare();
            Assert.Equal(0, prepared);
            Assert.Equal(0, started);
            Assert.Equal(ScanState.Error, scan.State);
            Assert.False(scan.IsBusy);
            Assert.Equal(2, scan.Duplicates.Count);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally {
            if (scan.IsBusy) await scan.CompleteWebScanAsync(true);
        }
    }

    [Fact]
    public async Task SwitchingScanFoldersInvalidatesBothOldAndDestinationSnapshots() {
        Seed("old-folder-a.mp4"); Seed("old-folder-b.mp4");
        ScanEngine.SaveDatabase();
        await scan.CompleteWebScanAsync(false);
        await scan.RemoveFromResultsAsync([]);
        string destination = Path.Combine(folder, "other-state");
        Directory.CreateDirectory(destination);
        foreach (var name in new[] { "WebResults.json", "WebResults.json.bak", "ScannedFiles.db" })
            File.Copy(Path.Combine(folder, name), Path.Combine(destination, name));
        scan.Settings.CustomDatabaseFolder = destination;
        Assert.True(scan.SaveSettings());
        scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
        int started = 0;
        scan.StartSearchOverride = () => {
            started++;
            AssertEmptySavedResults(folder);
            AssertEmptySavedResults(destination);
        };
        try {
            await scan.StartScanAndCompare();
            Assert.Equal(1, started);
            await scan.CompleteWebScanAsync(true);
            foreach (var stateFolder in new[] { folder, destination }) {
                using var restarted = NewService();
                restarted.Settings.CustomDatabaseFolder = stateFolder;
                await restarted.InitializeWebAutomationAsync();
                Assert.Empty(restarted.Duplicates);
            }
        }
        finally {
            if (scan.IsBusy) await scan.CompleteWebScanAsync(true);
        }
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("reset")]
    [InlineData("remove")]
    public async Task ExplicitResultClearSurvivesRestartAndRetainsMedia(string action) {
        var a = Seed("a.mp4"); var b = Seed("b.mp4");
        var config = await scan.LoadAutomationAsync();
        config.Selection.AutoAfterScan = true;
        await scan.SaveAutomationAsync(config);
        ScanEngine.SaveDatabase();
        await scan.CompleteWebScanAsync(false);
        // A seeded completed result set still uses the production persistence
        // path used after file operations, without simulating a scanner callback.
        await scan.RemoveFromResultsAsync([]);
        Assert.True(File.Exists(Path.Combine(folder, "WebResults.json")));
        if (action == "clear") await scan.ClearDatabaseAsync();
        else if (action == "reset") await scan.ResetAsync();
        else await scan.RemoveFromResultsAsync([a]);
        Assert.Empty(scan.Duplicates);
        using var restarted = NewService();
        await restarted.InitializeWebAutomationAsync();
        Assert.Empty(restarted.Duplicates);
        Assert.Equal(action == "remove" ? ScanState.Done : ScanState.Idle, restarted.State);
        Assert.Equal(action == "clear" ? 0 : 2, restarted.DatabaseEntryCount);
        Assert.True(File.Exists(a.Path)); Assert.True(File.Exists(b.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairDiagnosticUsesCurrentSettingsAfterDetachedScanOrReload(bool reload) {
        var a = Seed("a.mp4"); var b = Seed("b.mp4");
        // A completed scan owns a detached settings snapshot. Exercise the
        // production diagnostic text, not just the engine's field assignment.
        scan.Engine.Settings = new Settings { ThumbnailCount = 1 };
        if (reload) {
            var writer = new WebSettingsService();
            var saved = new Settings();
            Assert.True(writer.Load(saved));
            saved.ThumbnailCount = 7;
            Assert.True(writer.Save(saved));
            Assert.True(scan.ReloadSettings());
        }
        else scan.Settings.ThumbnailCount = 7;
        string report = await scan.TestFilePairAsync(a.Path, b.Path);
        Assert.Contains("Sampling 7 frame(s)", report);
        Assert.NotSame(scan.Settings, scan.Engine.Settings);
    }
}
