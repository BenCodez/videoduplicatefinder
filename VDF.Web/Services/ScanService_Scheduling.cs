// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using VDF.Core;
using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Scheduling;
using VDF.Core.Utils;

namespace VDF.Web.Services;

public sealed partial class ScanService {
    bool _terminalProcessing;
    DateTimeOffset? _scheduledRunStarted;
    IDisposable? _scheduledRunLease;
    readonly SemaphoreSlim _scheduledFinishGate = new(1, 1);
    internal const string ScheduledRunLeaseFileName = "WebSchedule.run";
    internal Action? ScheduledRunClaimed { get; set; }
    public async Task CheckScheduleAsync(DateTimeOffset now) {
        if (IsBusy) return;
        // Idle polling must not reset an error, replace a preview or flash the
        // scanning state when no scheduled work is due.
        var config = await AutomationStore.LoadAsync();
        if (config.Revision.Length == 0) return;
        if (!config.Schedule.Enabled && (_startupChecked || !config.ScanAtStartup)) { _startupChecked = true; return; }
        string folder = ConfiguredStateFolder(ReadScheduledSettings());
        var state = await ReadScheduleStateAsync(Path.Combine(folder, "WebSchedule.json"));
        ScheduleState = state;
        var evaluation = ScanSchedulePolicy.Evaluate(config.Schedule, now, state.LastClaimedSlotUtc, TimeZoneInfo.Local);
        if (evaluation.Error is not null) { AutomationStatus = evaluation.Error; return; }
        if (evaluation.DueSlotUtc is null && (_startupChecked || !config.ScanAtStartup)) {
            _startupChecked = true;
            return;
        }
        await StartWebScanAsync(true, now.ToUniversalTime());
    }

    async Task<bool> PrepareWebScanAsync(bool scheduled, DateTimeOffset now) {
        _runAutomation = null;
        ClearSelectionPlan();
        _scheduledStateFolder = null;
        _scheduledRunStarted = null;
        _engine.Settings = scheduled ? ReadScheduledSettings() : SnapshotConfiguredSettings();
        if (File.Exists(AutomationStore.Path) || File.Exists(AutomationStore.Path + ".bak") || scheduled) {
            string folder = ConfiguredStateFolder(_engine.Settings);
            _runAutomation = await LoadAutomationCoreAsync(folder);
            _cts.Token.ThrowIfCancellationRequested();
            if (scheduled) {
                IDisposable? runLease = null;
                try {
                    // Take the lifetime lease before the durable claim. A restart
                    // can then distinguish an abandoned claim from a live process.
                    runLease = JournalWriteLease.Acquire(Path.Combine(folder, ScheduledRunLeaseFileName));
                    string path = Path.Combine(folder, "WebSchedule.json");
                    WebScheduleState state;
                    using (JournalWriteLease.Acquire(path)) {
                        state = await ReadScheduleStateAsync(path);
                        var evaluation = ScanSchedulePolicy.Evaluate(_runAutomation.Schedule, now, state.LastClaimedSlotUtc, TimeZoneInfo.Local);
                        if (evaluation.Error is not null) throw new InvalidDataException(evaluation.Error);
                        bool startup = !_startupChecked && _runAutomation.ScanAtStartup;
                        _startupChecked = true;
                        ScheduleState = state;
                        if (evaluation.DueSlotUtc is null && !startup) return false;
                        ApplyScheduledProfile(_engine.Settings, _runAutomation.ScheduledScanProfile);
                        ValidateScheduledPrerequisites(_engine.Settings);
                        _cts.Token.ThrowIfCancellationRequested();
                        state.LastClaimedSlotUtc = evaluation.DueSlotUtc ?? state.LastClaimedSlotUtc;
                        state.LastStartedUtc = now;
                        state.LastCompletedUtc = null;
                        state.Status = "Starting";
                        state.DurationSeconds = 0;
                        await DurableJsonFile.WriteAsync(path, state, WebAutomationJsonContext.Default.WebScheduleState, ValidateScheduleState);
                    }
                    // Once ownership is visible, a terminal callback can write
                    // immediately; startup must no longer own the journal writer.
                    lock (_operationSync) {
                        _scheduledStateFolder = folder;
                        _scheduledRunStarted = now;
                        _scheduledRunLease = runLease;
                        runLease = null;
                        ScheduleState = state;
                    }
                    ScheduledRunClaimed?.Invoke();
                }
                finally { runLease?.Dispose(); }
            }
            await new CleanupProtectionGuard(folder).RecoverAsync();
        }
        return true;
    }

    internal async Task CompleteWebScanAsync(bool aborted) {
        lock (_operationSync) {
            if (_terminalProcessing) return;
            _terminalProcessing = true;
        }
        try {
            if (!aborted) {
                CaptureCleanupSnapshots(DatabaseUtils.GetDatabaseFolderPath());
                // Preserve completed scan output even if optional settings or
                // selection processing subsequently require restoration.
                await PersistResultsAsync();
                if (_runAutomation is not null) {
                    var current = await LoadAutomationCoreAsync(ResultsStateFolder());
                    if (current.Selection.AutoAfterScan) {
                        // Scheduled/automatic processing has no browser-highlighted scope.
                        current.Selection.HighlightedGroupsOnly = false;
                        await PreviewSelectionCoreAsync(current);
                    }
                }
            }
            await FinishScheduledStateAsync(aborted ? "Aborted" : "Completed");
            lock (_operationSync) State = aborted ? ScanState.Aborted : ScanState.Done;
        }
        catch (Exception ex) {
            SetError(ex);
            await FinishScheduledStateAsync("Failed: " + ex.Message);
        }
        finally {
            lock (_operationSync) { _scanRunActive = false; LastProgress = null; }
            NotifyAutomationChanged();
        }
    }

    static async Task<WebScheduleState> ReadScheduleStateAsync(string path) {
        var value = await DurableJsonFile.ReadPrimaryAsync(path, WebAutomationJsonContext.Default.WebScheduleState, ValidateScheduleState);
        if (value is null && SafePaths.Exists(path + ".bak"))
            throw new InvalidDataException("The schedule claim journal is missing. Restore it before scheduled work.");
        return value ?? new();
    }
    static bool ValidateScheduleState(WebScheduleState value) => value.Version == 1 && value.Status is not null &&
        value.Status.Length <= 32768 && double.IsFinite(value.DurationSeconds) && value.DurationSeconds >= 0 &&
        (value.LastClaimedSlotUtc is null || value.LastClaimedSlotUtc.Value.Offset == TimeSpan.Zero) &&
        (value.LastStartedUtc is null || value.LastStartedUtc.Value.Offset == TimeSpan.Zero) &&
        (value.LastCompletedUtc is null || value.LastCompletedUtc.Value.Offset == TimeSpan.Zero);

    internal static async Task<WebScheduleState> ReconcileScheduledRunAtStartupAsync(string folder) {
        SafePaths.RequireDirectory(folder);
        string path = Path.Combine(folder, "WebSchedule.json");
        IDisposable runLease;
        try { runLease = JournalWriteLease.Acquire(Path.Combine(folder, ScheduledRunLeaseFileName)); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33) {
            // A cooperating process still owns this run. Only sharing/lock
            // contention is benign; unsafe paths and other I/O errors propagate.
            return await ReadScheduleStateAsync(path);
        }
        using (runLease) {
            using var lease = JournalWriteLease.Acquire(path);
            // Read the current generation under both leases: a live process may
            // have finished just before the run lease became available.
            var state = await ReadScheduleStateAsync(path);
            if (state.Status != "Starting" || state.LastCompletedUtc is not null) return state;
            if (state.LastStartedUtc is null)
                throw new InvalidDataException("The starting schedule journal has no start time; it was preserved.");
            state.Status = "Interrupted";
            state.LastCompletedUtc = DateTimeOffset.UtcNow;
            state.DurationSeconds = Math.Max(0, (state.LastCompletedUtc.Value - state.LastStartedUtc.Value).TotalSeconds);
            await DurableJsonFile.WriteAsync(path, state, WebAutomationJsonContext.Default.WebScheduleState, ValidateScheduleState);
            return state;
        }
    }

    async Task FinishScheduledStateAsync(string status) {
        await _scheduledFinishGate.WaitAsync();
        string? folder;
        DateTimeOffset? started;
        IDisposable? runLease;
        lock (_operationSync) {
            // Detach one generation atomically. An early abort with no claim
            // must not clear ownership transferred while its callback unwinds.
            folder = _scheduledStateFolder;
            started = _scheduledRunStarted;
            runLease = _scheduledRunLease;
            _scheduledStateFolder = null;
            _scheduledRunStarted = null;
            _scheduledRunLease = null;
        }
        try {
            if (folder is null || started is null) return;
            string path = Path.Combine(folder, "WebSchedule.json");
            using var lease = JournalWriteLease.Acquire(path);
            var state = await ReadScheduleStateAsync(path);
            // A different process's newer run must not be overwritten by a late
            // terminal callback from this process.
            if (state.LastStartedUtc != started) return;
            state.Status = status;
            state.LastCompletedUtc = DateTimeOffset.UtcNow;
            state.DurationSeconds = Math.Max(0, (state.LastCompletedUtc.Value - started.Value).TotalSeconds);
            await DurableJsonFile.WriteAsync(path, state, WebAutomationJsonContext.Default.WebScheduleState, ValidateScheduleState);
            ScheduleState = state;
        }
        catch (Exception ex) { AutomationStatus = "Schedule completion could not be saved: " + ex.Message; }
        finally {
            try { runLease?.Dispose(); }
            finally { _scheduledFinishGate.Release(); }
        }
    }

    static void ApplyScheduledProfile(Settings s, WebScanProfile profile) {
        if (profile == WebScanProfile.Custom) return;
        bool extended = profile != WebScanProfile.ExactAndNear;
        s.Percent = extended ? 92 : 98;
        s.CompareHorizontallyFlipped = extended;
        s.IgnoreBlackPixels = extended;
        s.IgnoreWhitePixels = extended;
        s.UseAiMatching = profile is WebScanProfile.AiScan or WebScanProfile.DeepClean;
        s.EnableAiPartialDetection = s.UseAiMatching;
        s.EnablePartialClipDetection = profile == WebScanProfile.DeepClean;
    }
    static Settings ReadScheduledSettings() {
        var settings = new Settings();
        if (!new WebSettingsService().Load(settings))
            throw new InvalidDataException("Save valid scan settings before enabling scheduled scans.");
        return settings;
    }
    Settings SnapshotConfiguredSettings() => JsonSerializer.Deserialize(JsonSerializer.Serialize(Settings,
        WebAutomationJsonContext.Default.Settings), WebAutomationJsonContext.Default.Settings)!;
    static void ValidateScheduledPrerequisites(Settings settings) {
        if (settings.IncludeList.Count == 0 || settings.IncludeList.Any(p => !Directory.Exists(p)))
            throw new IOException("An included scan folder is missing or no folders are configured.");
        if ((settings.UseNativeFfmpegBinding ? !ScanEngine.NativeFFmpegExists : !ScanEngine.FFmpegExists) || !ScanEngine.FFprobeExists)
            throw new IOException("FFmpeg and FFprobe must be installed before scheduling scans.");
        if (settings.NeedsAiComponents && !VDF.Core.AI.AiComponents.IsReady)
            throw new IOException("Install AI components with a manual scan before enabling an AI schedule.");
        if (settings.MaxDegreeOfParallelism == 0 || settings.FilterByFileSize && settings.MaximumFileSize <= settings.MinimumFileSize)
            throw new IOException("The scan concurrency or file size filters are invalid.");
    }
}

/// <summary>Owns scheduling in the server process; no browser circuit is required.</summary>
public sealed class WebAutomationHostedService(ScanService scan, ILogger<WebAutomationHostedService> logger) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try { await scan.InitializeWebAutomationAsync(); }
        catch (Exception ex) { logger.LogError(ex, "Web automation initialization failed; existing state was retained"); }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do {
            try { await scan.CheckScheduleAsync(DateTimeOffset.UtcNow); }
            catch (Exception ex) { logger.LogError(ex, "Web scheduled scan could not start"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
