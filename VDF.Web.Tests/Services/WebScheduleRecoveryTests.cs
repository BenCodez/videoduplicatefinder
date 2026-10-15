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
        Assert.Equal(before, await File.ReadAllBytesAsync(JournalPath));
        if (symbolicLink) Assert.Equal("preserve", await File.ReadAllTextAsync(target));
        if (!runLease) {
            using var released = JournalWriteLease.Acquire(RunLeasePath);
        }
    }
}
