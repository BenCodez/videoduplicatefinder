// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using VDF.Core;
using VDF.Core.Review;
using VDF.Core.Scheduling;
using VDF.Core.Utils;
using VDF.Web.Services;

namespace VDF.Web.Tests.Services;

public sealed class LinuxScheduleLeaseTheoryAttribute : TheoryAttribute {
    public LinuxScheduleLeaseTheoryAttribute() {
        if (!OperatingSystem.IsLinux()) Skip = "The symbolic-link lease fixture requires Linux.";
    }
}

[Collection("WebSettingsOverride")]
public sealed class WebScheduleRecoveryTests : IAsyncLifetime {
    readonly string folder = Path.Combine(Path.GetTempPath(), "VDF.WebScheduleRecovery." + Guid.NewGuid().ToString("N"));
    readonly ScanService scan;
    static readonly DateTimeOffset Started = new(2026, 1, 1, 12, 1, 0, TimeSpan.Zero);
    string JournalPath => Path.Combine(folder, "WebSchedule.json");
    string RunLeasePath => Path.Combine(folder, ScanService.ScheduledRunLeaseFileName);

    public WebScheduleRecoveryTests() {
        Directory.CreateDirectory(folder);
        WebSettingsService.TestOverrideSettingsPath = Path.Combine(folder, "web-settings.json");
        scan = NewService();
    }
    ScanService NewService() {
        var service = new ScanService(new WebSettingsService());
        service.Settings.CustomDatabaseFolder = folder;
        service.Settings.IncludeList.Add(folder);
        return service;
    }
    public async Task InitializeAsync() {
        await ScanEngine.LoadDatabase(folder);
        DatabaseUtils.Database.Clear();
        ScanEngine.SaveDatabase();
        var config = await scan.LoadAutomationAsync();
        config.Schedule = new() { Enabled = true, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        await scan.SaveAutomationAsync(config);
        Assert.True(scan.SaveSettings());
        scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
    }
    public async Task DisposeAsync() {
        if (scan.IsBusy) await scan.CompleteWebScanAsync(true);
        scan.Dispose();
        WebSettingsService.TestOverrideSettingsPath = null;
        Directory.Delete(folder, true);
    }

    static WebScheduleState StartingState() => new() {
        LastClaimedSlotUtc = Started.AddMinutes(-1), LastStartedUtc = Started, Status = "Starting"
    };
    Task WriteStateAsync(WebScheduleState state) => DurableJsonFile.WriteAsync(JournalPath, state,
        WebAutomationJsonContext.Default.WebScheduleState, _ => true);
    async Task<WebScheduleState> ReadStateAsync() => JsonSerializer.Deserialize(await File.ReadAllTextAsync(JournalPath),
        WebAutomationJsonContext.Default.WebScheduleState)!;

    [Theory]
    [InlineData(false, "include")]
    [InlineData(true, "include")]
    [InlineData(false, "concurrency")]
    [InlineData(true, "concurrency")]
    [InlineData(false, "filters")]
    [InlineData(true, "filters")]
    [InlineData(false, "claim")]
    [InlineData(true, "claim")]
    public async Task UnclaimedStartupScanRetriesAfterPrerequisitesOrClaimStorageRecover(bool recurring, string fault) {
        var config = await scan.LoadAutomationAsync(); config.ScanAtStartup = true;
        config.Schedule = new() { Enabled = recurring, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        await scan.SaveAutomationAsync(config);
        string included = Path.Combine(folder, "temporarily-unavailable");
        if (fault == "include") scan.Settings.IncludeList.Add(included);
        if (fault == "concurrency") scan.Settings.MaxDegreeOfParallelism = 0;
        if (fault == "filters") {
            scan.Settings.FilterByFileSize = true; scan.Settings.MinimumFileSize = 10; scan.Settings.MaximumFileSize = 10;
        }
        Assert.True(scan.SaveSettings());
        if (fault == "claim") {
            await WriteStateAsync(new());
        }
        string media = Path.Combine(folder, "preserved.mp4"); await File.WriteAllBytesAsync(media, [1, 2, 3, 4]);
        int prepared = 0, searches = 0;
        scan.PrepareScanAsync = (_, _) => { prepared++; return Task.CompletedTask; };
        scan.StartSearchOverride = () => searches++;
        var previousObserver = DurableFileOperations.FileMovedObserver.Value;
        if (fault == "claim") DurableFileOperations.FileMovedObserver.Value = (_, destination) => {
            if (PathComparer.ForCurrentPlatform.Equals(destination, JournalPath + ".bak"))
                throw new IOException("Synthetic failure before the mirrored claim completes");
        };
        try { await scan.CheckScheduleAsync(Started); }
        finally { DurableFileOperations.FileMovedObserver.Value = previousObserver; }
        Assert.Equal(ScanState.Error, scan.State); Assert.False(scan.IsBusy);
        Assert.Equal(0, prepared); Assert.Equal(0, searches);
        Assert.Null(scan.ScheduleState?.LastStartedUtc);
        if (File.Exists(JournalPath)) Assert.Null((await ReadStateAsync()).LastStartedUtc);
        using (JournalWriteLease.Acquire(RunLeasePath)) { }
        if (fault == "include") Directory.CreateDirectory(included);
        if (fault == "concurrency") scan.Settings.MaxDegreeOfParallelism = 1;
        if (fault == "filters") scan.Settings.MaximumFileSize = 20;
        Assert.True(scan.SaveSettings());
        await scan.CheckScheduleAsync(Started.AddSeconds(15));
        Assert.Equal(1, prepared); Assert.Equal(1, searches); Assert.True(scan.IsBusy);
        Assert.Equal(Started.AddSeconds(15), (await ReadStateAsync()).LastStartedUtc);
        await scan.CompleteWebScanAsync(false);
        await scan.CheckScheduleAsync(Started.AddSeconds(30));
        Assert.Equal(1, searches); Assert.False(scan.IsBusy);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(media));
    }

    [Theory]
    [InlineData(false, "prepare")]
    [InlineData(true, "prepare")]
    [InlineData(false, "callback")]
    [InlineData(true, "callback")]
    [InlineData(false, "abort")]
    [InlineData(true, "abort")]
    public async Task ClaimedStartupScanDoesNotRetryAfterPostClaimFailureOrAbort(bool recurring, string fault) {
        var config = await scan.LoadAutomationAsync(); config.ScanAtStartup = true;
        config.Schedule = new() { Enabled = recurring, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        await scan.SaveAutomationAsync(config);
        int claims = 0, searches = 0;
        scan.ScheduledRunClaimed = () => {
            claims++;
            if (fault == "callback") throw new IOException("Synthetic post-claim failure");
            if (fault == "abort") scan.Stop();
        };
        scan.PrepareScanAsync = (_, _) => fault == "prepare" ? Task.FromException(new IOException("Synthetic preparation failure")) : Task.CompletedTask;
        scan.StartSearchOverride = () => searches++;
        await scan.CheckScheduleAsync(Started);
        Assert.Equal(1, claims); Assert.Equal(0, searches); Assert.False(scan.IsBusy);
        Assert.Equal(fault == "abort" ? ScanState.Aborted : ScanState.Error, scan.State);
        var before = await ReadStateAsync(); Assert.Equal(Started, before.LastStartedUtc);
        Assert.NotNull(before.LastCompletedUtc);
        await scan.CheckScheduleAsync(Started.AddSeconds(15));
        Assert.Equal(1, claims); Assert.Equal(0, searches); Assert.False(scan.IsBusy);
        Assert.Equal(before.LastStartedUtc, (await ReadStateAsync()).LastStartedUtc);
        using var lease = JournalWriteLease.Acquire(RunLeasePath);
    }

    [Fact]
    public async Task StartupTerminatesOrphanAndPreservesClaimWithoutRepeatingItsSlot() {
        string media = Path.Combine(folder, "preserved.mp4");
        await File.WriteAllBytesAsync(media, [1, 2, 3, 4]);
        await WriteStateAsync(StartingState());
        using var restarted = NewService();
        await restarted.InitializeWebAutomationAsync();
        var state = Assert.IsType<WebScheduleState>(restarted.ScheduleState);
        Assert.Equal("Interrupted", state.Status);
        Assert.Equal(Started.AddMinutes(-1), state.LastClaimedSlotUtc);
        Assert.Equal(Started, state.LastStartedUtc);
        Assert.NotNull(state.LastCompletedUtc);
        Assert.True(state.DurationSeconds >= 0);
        foreach (string suffix in new[] { "", ".bak" }) {
            var saved = JsonSerializer.Deserialize(await File.ReadAllTextAsync(JournalPath + suffix),
                WebAutomationJsonContext.Default.WebScheduleState)!;
            Assert.Equal("Interrupted", saved.Status);
            Assert.Equal(state.LastCompletedUtc, saved.LastCompletedUtc);
        }
        int searches = 0;
        restarted.StartSearchOverride = () => searches++;
        await restarted.CheckScheduleAsync(Started.AddMinutes(1));
        Assert.Equal(0, searches);
        Assert.False(restarted.IsBusy);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(media));
        using var released = JournalWriteLease.Acquire(RunLeasePath);
    }

    [Fact]
    public async Task CompletedGenerationIsNotRewritten() {
        var completed = StartingState();
        completed.Status = "Completed";
        completed.LastCompletedUtc = Started.AddSeconds(45);
        completed.DurationSeconds = 45;
        await WriteStateAsync(completed);
        byte[] before = await File.ReadAllBytesAsync(JournalPath);
        var result = await ScanService.ReconcileScheduledRunAtStartupAsync(folder);
        Assert.Equal("Completed", result.Status);
        Assert.Equal(completed.LastCompletedUtc, result.LastCompletedUtc);
        Assert.Equal(before, await File.ReadAllBytesAsync(JournalPath));
    }

    [Fact]
    public async Task BusyRunLeaseKeepsLiveStartingGeneration() {
        await WriteStateAsync(StartingState());
        byte[] before = await File.ReadAllBytesAsync(JournalPath);
        // Exercise the platform's actual FileShare.None contention result.
        using (JournalWriteLease.Acquire(RunLeasePath)) {
            var result = await ScanService.ReconcileScheduledRunAtStartupAsync(folder);
            Assert.Equal("Starting", result.Status);
            Assert.Null(result.LastCompletedUtc);
            Assert.Equal(before, await File.ReadAllBytesAsync(JournalPath));
        }
        Assert.Equal("Interrupted", (await ScanService.ReconcileScheduledRunAtStartupAsync(folder)).Status);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task HeldScheduledRunLeaseSkipsWithoutErrorAndRetainsUnclaimedEligibility(bool startupOnly, bool existingResults, bool previousClaim) {
        var config = await scan.LoadAutomationAsync(); config.ScanAtStartup = startupOnly;
        config.Schedule = config.Schedule with { Enabled = !startupOnly }; await scan.SaveAutomationAsync(config);
        if (existingResults) {
            var group = Guid.NewGuid();
            foreach (string name in new[] { "old-a.mp4", "old-b.mp4" }) {
                string path = Path.Combine(folder, name); File.WriteAllBytes(path, [1, 2, 3, 4]);
                var entry = new FileEntry(path); DatabaseUtils.Database.Add(entry);
                scan.Engine.Duplicates.Add(new VDF.Core.ViewModels.DuplicateItem(entry, .01f, group, DuplicateFlags.None));
            }
            ScanEngine.SaveDatabase(); scan.CaptureCleanupSnapshots(folder); await scan.RemoveFromResultsAsync([]);
        }
        if (previousClaim) await WriteStateAsync(new() { Status = "Completed", LastClaimedSlotUtc = Started.AddMinutes(-6),
            LastStartedUtc = Started.AddMinutes(-5), LastCompletedUtc = Started.AddMinutes(-4), DurationSeconds = 60 });
        byte[]? journalBefore = File.Exists(JournalPath) ? File.ReadAllBytes(JournalPath) : null;
        string resultsPath = Path.Combine(folder, "WebResults.json");
        byte[]? resultsBefore = File.Exists(resultsPath) ? File.ReadAllBytes(resultsPath) : null;
        int prepared = 0, searches = 0;
        scan.PrepareScanAsync = (_, _) => { prepared++; return Task.CompletedTask; };
        scan.StartSearchOverride = () => searches++;
        using (JournalWriteLease.Acquire(RunLeasePath)) {
            for (int retry = 0; retry < 2; retry++) {
                Assert.Null(await scan.CheckScheduleAsync(Started.AddSeconds(retry)));
                Assert.Equal(existingResults ? ScanState.Done : ScanState.Idle, scan.State); Assert.Null(scan.ErrorMessage); Assert.False(scan.IsBusy);
                Assert.Equal(0, searches); Assert.Equal(0, prepared); Assert.Equal(existingResults ? 2 : 0, scan.Duplicates.Count);
                Assert.Equal(journalBefore, File.Exists(JournalPath) ? File.ReadAllBytes(JournalPath) : null);
                Assert.Equal(resultsBefore, File.Exists(resultsPath) ? File.ReadAllBytes(resultsPath) : null);
                Assert.Throws<IOException>(() => JournalWriteLease.Acquire(RunLeasePath));
            }
        }
        await scan.CheckScheduleAsync(Started.AddSeconds(15));
        Assert.True(scan.IsBusy); Assert.Equal(1, prepared); Assert.Equal(1, searches);
        await scan.CompleteWebScanAsync(false);
        await scan.CheckScheduleAsync(Started.AddSeconds(30));
        Assert.False(scan.IsBusy); Assert.Equal(1, searches);
        using var released = JournalWriteLease.Acquire(RunLeasePath);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HeldScheduledRunLeasePreservesAnotherOwnersCommittedGeneration(bool startupOnly, bool completed) {
        var config = await scan.LoadAutomationAsync(); config.ScanAtStartup = startupOnly;
        config.Schedule = config.Schedule with { Enabled = !startupOnly }; await scan.SaveAutomationAsync(config);
        var state = StartingState();
        if (completed) { state.Status = "Completed"; state.LastCompletedUtc = Started.AddMinutes(1); }
        await WriteStateAsync(state); byte[] before = File.ReadAllBytes(JournalPath);
        scan.PrepareScanAsync = (_, _) => throw new InvalidOperationException("A held run must not prepare another scan");
        scan.StartSearchOverride = () => throw new InvalidOperationException("A held run must not start another scan");
        using (JournalWriteLease.Acquire(RunLeasePath)) {
            Assert.Null(await scan.CheckScheduleAsync(Started.AddMinutes(6)));
            Assert.Equal(ScanState.Idle, scan.State); Assert.Null(scan.ErrorMessage); Assert.False(scan.IsBusy);
            Assert.Equal(state.Status, scan.ScheduleState?.Status); Assert.Equal(state.LastStartedUtc, scan.ScheduleState?.LastStartedUtc);
            Assert.Equal(state.LastCompletedUtc, scan.ScheduleState?.LastCompletedUtc);
            Assert.Equal(before, File.ReadAllBytes(JournalPath));
            Assert.Throws<IOException>(() => JournalWriteLease.Acquire(RunLeasePath));
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task SharedScanLeaseSerializesManualAndScheduledOwners(bool ownerScheduled, bool contenderScheduled, bool missingConfig) {
        using var contender = NewService(); contender.PrepareScanAsync = (_, _) => Task.CompletedTask;
        int ownerStarts = 0, contenderStarts = 0;
        scan.StartSearchOverride = () => ownerStarts++;
        contender.StartSearchOverride = () => contenderStarts++;
        if (missingConfig && !ownerScheduled) { File.Delete(Path.Combine(folder, "web-settings.automation.json")); File.Delete(Path.Combine(folder, "web-settings.automation.json.bak")); }
        try {
            if (ownerScheduled) await scan.CheckScheduleAsync(Started); else await scan.StartScanAndCompare();
            Assert.True(scan.IsBusy); Assert.Equal(1, ownerStarts);
            Assert.Throws<IOException>(() => JournalWriteLease.Acquire(RunLeasePath));
            if (missingConfig) { File.Delete(Path.Combine(folder, "web-settings.automation.json")); File.Delete(Path.Combine(folder, "web-settings.automation.json.bak")); }
            byte[]? before = File.Exists(JournalPath) ? File.ReadAllBytes(JournalPath) : null;
            if (contenderScheduled) {
                Assert.Null(await contender.CheckScheduleAsync(Started.AddMinutes(6)));
                Assert.Equal(ScanState.Idle, contender.State); Assert.Null(contender.ErrorMessage);
            }
            else {
                await contender.StartScanAndCompare();
                Assert.Equal(ScanState.Error, contender.State); Assert.Contains("Another scan", contender.ErrorMessage);
            }
            Assert.False(contender.IsBusy); Assert.Equal(0, contenderStarts);
            Assert.Equal(before, File.Exists(JournalPath) ? File.ReadAllBytes(JournalPath) : null);
            Assert.Throws<IOException>(() => JournalWriteLease.Acquire(RunLeasePath));
            await scan.CompleteWebScanAsync(false);
            if (contenderScheduled) await contender.CheckScheduleAsync(Started.AddMinutes(6)); else await contender.StartScanAndCompare();
            Assert.True(contender.IsBusy); Assert.Equal(1, contenderStarts);
            Assert.Throws<IOException>(() => JournalWriteLease.Acquire(RunLeasePath));
            await contender.CompleteWebScanAsync(false);
            using var released = JournalWriteLease.Acquire(RunLeasePath);
        }
        finally {
            if (scan.IsBusy) await scan.CompleteWebScanAsync(true);
            if (contender.IsBusy) await contender.CompleteWebScanAsync(true);
        }
    }

    public static IEnumerable<object[]> ManualLeaseOutcomes() {
        foreach (string outcome in new[] { "complete", "abort", "prepare", "engine", "early", "stop", "dispose" })
            foreach (bool missing in new[] { false, true }) yield return [outcome, missing];
    }
    [Theory]
    [MemberData(nameof(ManualLeaseOutcomes))]
    public async Task SharedScanLeaseManualRunOwnsLifetimeAndReleasesAfterEveryTerminalPath(string outcome, bool missingConfig) {
        if (missingConfig) { File.Delete(Path.Combine(folder, "web-settings.automation.json")); File.Delete(Path.Combine(folder, "web-settings.automation.json.bak")); }
        Task? completion = null;
        bool prepareHeld = false, engineHeld = false;
        bool IsHeld() {
            try { using var probe = JournalWriteLease.Acquire(RunLeasePath); return false; }
            catch (IOException) { return true; }
        }
        scan.PrepareScanAsync = (_, token) => {
            prepareHeld = IsHeld();
            if (outcome == "prepare") throw new IOException("Synthetic manual preparation failure");
            if (outcome is "stop" or "dispose") {
                if (outcome == "stop") scan.Stop(); else scan.Dispose();
                return Task.FromCanceled(token);
            }
            return Task.CompletedTask;
        };
        scan.StartSearchOverride = () => {
            engineHeld = IsHeld();
            if (outcome == "engine") throw new IOException("Synthetic manual engine startup failure");
            if (outcome == "early") completion = scan.CompleteWebScanAsync(false);
        };
        await scan.StartScanAndCompare();
        if (completion is not null) await completion;
        if (scan.IsBusy) {
            Assert.Throws<IOException>(() => JournalWriteLease.Acquire(RunLeasePath));
            await scan.CompleteWebScanAsync(outcome == "abort");
        }
        Assert.Equal(outcome is "prepare" or "engine" ? ScanState.Error : outcome is "abort" or "stop" or "dispose" ? ScanState.Aborted : ScanState.Done, scan.State);
        Assert.True(prepareHeld);
        if (outcome is not ("prepare" or "stop" or "dispose")) Assert.True(engineHeld);
        Assert.False(scan.IsBusy); Assert.False(File.Exists(JournalPath));
        using var released = JournalWriteLease.Acquire(RunLeasePath);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SharedScanLeaseTerminalCompletionDuringPreparationRetainsOwnershipUntilStartupUnwinds(bool scheduled, bool dispose) {
        scan.PrepareScanAsync = async (_, token) => {
            if (dispose) scan.Dispose(); else scan.Stop();
            await scan.CompletionTask;
            Assert.True(scan.IsBusy);
            Assert.Throws<IOException>(() => JournalWriteLease.Acquire(RunLeasePath));
            token.ThrowIfCancellationRequested();
        };
        if (scheduled) await scan.CheckScheduleAsync(Started); else await scan.StartScanAndCompare();
        Assert.False(scan.IsBusy); Assert.Equal(ScanState.Aborted, scan.State);
        using var released = JournalWriteLease.Acquire(RunLeasePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedScanLeaseConfigurationFailureReleasesOwnershipAndPreservesJournals(bool future) {
        string path = Path.Combine(folder, "web-settings.automation.json");
        if (future) { var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; node["version"] = 2; File.WriteAllText(path, node.ToJsonString()); }
        else File.WriteAllText(path, "{");
        byte[] before = File.ReadAllBytes(path);
        scan.StartSearchOverride = () => throw new InvalidOperationException("Invalid settings must not start a scan");
        await scan.StartScanAndCompare();
        Assert.Equal(ScanState.Error, scan.State); Assert.False(scan.IsBusy); Assert.False(File.Exists(JournalPath));
        Assert.Equal(before, File.ReadAllBytes(path)); using var released = JournalWriteLease.Acquire(RunLeasePath);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ActualRunOwnsLeaseUntilTerminalCompletion(bool aborted, bool finishDuringStart) {
        Task? completion = null;
        scan.StartSearchOverride = () => {
            Assert.Throws<IOException>(() => JournalWriteLease.Acquire(RunLeasePath));
            if (finishDuringStart) completion = scan.CompleteWebScanAsync(aborted);
        };
        await scan.CheckScheduleAsync(Started);
        if (!finishDuringStart) {
            var live = await ScanService.ReconcileScheduledRunAtStartupAsync(folder);
            Assert.Equal("Starting", live.Status);
            Assert.Null(live.LastCompletedUtc);
            completion = scan.CompleteWebScanAsync(aborted);
        }
        await completion!;
        Assert.Equal(aborted ? "Aborted" : "Completed", (await ReadStateAsync()).Status);
        Assert.False(scan.IsBusy);
        using var released = JournalWriteLease.Acquire(RunLeasePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationAndEngineFailureReleaseClaimedRunLease(bool engineFailure) {
        if (engineFailure) scan.StartSearchOverride = () => throw new IOException("Synthetic engine failure");
        else scan.PrepareScanAsync = (_, _) => Task.FromException(new IOException("Synthetic preparation failure"));
        await scan.CheckScheduleAsync(Started);
        Assert.Equal(ScanState.Error, scan.State);
        Assert.False(scan.IsBusy);
        var state = await ReadStateAsync();
        Assert.StartsWith("Failed:", state.Status);
        Assert.NotNull(state.LastCompletedUtc);
        Assert.Equal(Started.AddMinutes(-1), state.LastClaimedSlotUtc);
        using var released = JournalWriteLease.Acquire(RunLeasePath);
    }

    [Fact]
    public async Task PrerequisiteFailureBeforeClaimReleasesLocalRunLease() {
        string missing = Path.Combine(folder, "missing-media");
        scan.Settings.IncludeList.Clear();
        scan.Settings.IncludeList.Add(missing);
        Assert.True(scan.SaveSettings());
        int searches = 0;
        scan.StartSearchOverride = () => searches++;
        await scan.CheckScheduleAsync(Started);
        Assert.Equal(ScanState.Error, scan.State);
        Assert.False(scan.IsBusy);
        Assert.Equal(0, searches);
        Assert.False(File.Exists(JournalPath));
        using var released = JournalWriteLease.Acquire(RunLeasePath);
    }

    [Theory]
    [InlineData("initial", false)]
    [InlineData("claim", false)]
    [InlineData("owned", false)]
    [InlineData("prepare", false)]
    [InlineData("initial", true)]
    [InlineData("claim", true)]
    [InlineData("owned", true)]
    [InlineData("prepare", true)]
    public async Task StopDuringStartupDoesNotLeakRunLeaseOrLeaveStartingClaim(string stopAt, bool dispose) {
        bool stopped = false;
        int searches = 0;
        scan.StartSearchOverride = () => searches++;
        void StopOnce() {
            if (stopped) return;
            stopped = true;
            if (dispose) scan.Dispose(); else scan.Stop();
        }
        void OnStateChanged() {
            if (stopAt == "initial" && scan.State == ScanState.Scanning) StopOnce();
        }
        scan.StateChanged += OnStateChanged;
        if (stopAt == "owned") scan.ScheduledRunClaimed = StopOnce;
        DurableFileOperations.FileMovedObserver.Value = (_, destination) => {
            if (stopAt == "claim" && destination == JournalPath) StopOnce();
        };
        if (stopAt == "prepare") scan.PrepareScanAsync = (_, token) => {
            StopOnce();
            return Task.FromCanceled(token);
        };
        try {
            await scan.CheckScheduleAsync(Started);
            Assert.True(stopped);
            Assert.Equal(0, searches);
            Assert.False(scan.IsBusy);
            using var released = JournalWriteLease.Acquire(RunLeasePath);
            if (stopAt != "initial") {
                var state = await ReadStateAsync();
                Assert.Equal("Aborted", state.Status);
                Assert.NotNull(state.LastCompletedUtc);
                Assert.Equal(Started.AddMinutes(-1), state.LastClaimedSlotUtc);
            }
            else Assert.False(File.Exists(JournalPath));
        }
        finally {
            scan.StateChanged -= OnStateChanged;
            scan.ScheduledRunClaimed = null;
            DurableFileOperations.FileMovedObserver.Value = null;
        }
    }

    [Fact]
    public async Task JournalWriterContentionPreservesGenerationAndReleasesRecoveryLease() {
        await WriteStateAsync(StartingState());
        byte[] before = await File.ReadAllBytesAsync(JournalPath);
        using (JournalWriteLease.Acquire(JournalPath))
            await Assert.ThrowsAsync<IOException>(() => ScanService.ReconcileScheduledRunAtStartupAsync(folder));
        Assert.Equal(before, await File.ReadAllBytesAsync(JournalPath));
        using var released = JournalWriteLease.Acquire(RunLeasePath);
    }

    [Fact]
    public async Task ClaimWriterContentionBeforeOwnershipTransferReleasesRunLease() {
        await WriteStateAsync(new() { Status = "Completed" });
        byte[] before = await File.ReadAllBytesAsync(JournalPath);
        int searches = 0;
        scan.StartSearchOverride = () => searches++;
        using (JournalWriteLease.Acquire(JournalPath))
            await scan.CheckScheduleAsync(Started);
        Assert.Equal(ScanState.Error, scan.State);
        Assert.False(scan.IsBusy);
        Assert.Equal(0, searches);
        Assert.Equal(before, await File.ReadAllBytesAsync(JournalPath));
        using var released = JournalWriteLease.Acquire(RunLeasePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptClaimJournalIsPreservedAndRefusesRecovery(bool missing) {
        await WriteStateAsync(StartingState());
        if (missing) File.Delete(JournalPath);
        else await File.WriteAllTextAsync(JournalPath, "{");
        byte[] backup = await File.ReadAllBytesAsync(JournalPath + ".bak");
        var recoveryError = await Assert.ThrowsAnyAsync<Exception>(() => ScanService.ReconcileScheduledRunAtStartupAsync(folder));
        Assert.True(recoveryError is InvalidDataException or JsonException);
        Assert.Equal(backup, await File.ReadAllBytesAsync(JournalPath + ".bak"));
        if (missing) Assert.False(File.Exists(JournalPath));
        else Assert.Equal("{", await File.ReadAllTextAsync(JournalPath));
        int searches = 0;
        scan.StartSearchOverride = () => searches++;
        var startError = await Assert.ThrowsAnyAsync<Exception>(() => scan.CheckScheduleAsync(Started));
        Assert.True(startError is InvalidDataException or JsonException);
        Assert.Equal(0, searches);
        using var released = JournalWriteLease.Acquire(RunLeasePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UnsafeLeaseDirectoriesRefuseRecoveryWithoutChangingTheClaim(bool runLease) =>
        AssertUnsafeLeasePathAsync(runLease, symbolicLink: false);

    [LinuxScheduleLeaseTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UnsafeLeaseLinksRefuseRecoveryWithoutChangingTheClaim(bool runLease) =>
        AssertUnsafeLeasePathAsync(runLease, symbolicLink: true);

    async Task AssertUnsafeLeasePathAsync(bool runLease, bool symbolicLink) {
        await WriteStateAsync(StartingState());
        byte[] before = await File.ReadAllBytesAsync(JournalPath);
        string unsafePath = (runLease ? RunLeasePath : JournalPath) + ".lock";
        string target = Path.Combine(folder, "unrelated-lock-target");
        if (symbolicLink) {
            await File.WriteAllTextAsync(target, "preserve");
            File.CreateSymbolicLink(unsafePath, target);
        }
        else Directory.CreateDirectory(unsafePath);
        await Assert.ThrowsAnyAsync<IOException>(() => ScanService.ReconcileScheduledRunAtStartupAsync(folder));
        Assert.NotNull(await scan.CheckScheduleAsync(Started.AddMinutes(6)));
        Assert.Equal(ScanState.Error, scan.State); Assert.False(scan.IsBusy);
        Assert.Equal(before, await File.ReadAllBytesAsync(JournalPath));
        if (symbolicLink) Assert.Equal("preserve", await File.ReadAllTextAsync(target));
        if (!runLease) {
            using var released = JournalWriteLease.Acquire(RunLeasePath);
        }
    }
}
