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
