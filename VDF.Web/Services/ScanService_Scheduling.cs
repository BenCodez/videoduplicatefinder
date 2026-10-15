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
        LastSelectionPlan = null;
        _scheduledStateFolder = null;
        _scheduledRunStarted = null;
        _engine.Settings = scheduled ? ReadScheduledSettings() : SnapshotConfiguredSettings();
        if (File.Exists(AutomationStore.Path) || File.Exists(AutomationStore.Path + ".bak") || scheduled) {
            string folder = ConfiguredStateFolder(_engine.Settings);
            _runAutomation = await LoadAutomationCoreAsync(folder);
            if (scheduled) {
                string path = Path.Combine(folder, "WebSchedule.json");
                using var lease = JournalWriteLease.Acquire(path);
                var state = await ReadScheduleStateAsync(path);
                var evaluation = ScanSchedulePolicy.Evaluate(_runAutomation.Schedule, now, state.LastClaimedSlotUtc, TimeZoneInfo.Local);
                if (evaluation.Error is not null) throw new InvalidDataException(evaluation.Error);
                bool startup = !_startupChecked && _runAutomation.ScanAtStartup;
                _startupChecked = true;
                ScheduleState = state;
                if (evaluation.DueSlotUtc is null && !startup) return false;
                ApplyScheduledProfile(_engine.Settings, _runAutomation.ScheduledScanProfile);
                ValidateScheduledPrerequisites(_engine.Settings);
                state.LastClaimedSlotUtc = evaluation.DueSlotUtc ?? state.LastClaimedSlotUtc;
                state.LastStartedUtc = now;
                state.LastCompletedUtc = null;
                state.Status = "Starting";
                state.DurationSeconds = 0;
                await DurableJsonFile.WriteAsync(path, state, WebAutomationJsonContext.Default.WebScheduleState, ValidateScheduleState);
                _scheduledStateFolder = folder;
                _scheduledRunStarted = now;
                ScheduleState = state;
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
                if (_runAutomation is not null) {
                    var current = await LoadAutomationCoreAsync(ResultsStateFolder());
                    if (current.Selection.AutoAfterScan) {
                        // Scheduled/automatic processing has no browser-highlighted scope.
                        current.Selection.HighlightedGroupsOnly = false;
                        await PreviewSelectionCoreAsync(current);
                    }
                    await PersistResultsAsync();
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

    async Task FinishScheduledStateAsync(string status) {
        if (_scheduledStateFolder is null || _scheduledRunStarted is null) return;
        try {
            string path = Path.Combine(_scheduledStateFolder, "WebSchedule.json");
            using var lease = JournalWriteLease.Acquire(path);
            var state = await ReadScheduleStateAsync(path);
            // A different process's newer run must not be overwritten by a late
            // terminal callback from this process.
            if (state.LastStartedUtc != _scheduledRunStarted) return;
            state.Status = status;
            state.LastCompletedUtc = DateTimeOffset.UtcNow;
            state.DurationSeconds = Math.Max(0, (state.LastCompletedUtc.Value - _scheduledRunStarted.Value).TotalSeconds);
            await DurableJsonFile.WriteAsync(path, state, WebAutomationJsonContext.Default.WebScheduleState, ValidateScheduleState);
            ScheduleState = state;
        }
        catch (Exception ex) { AutomationStatus = "Schedule completion could not be saved: " + ex.Message; }
        finally { _scheduledStateFolder = null; _scheduledRunStarted = null; }
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
