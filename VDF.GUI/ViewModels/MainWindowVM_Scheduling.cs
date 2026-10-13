using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using VDF.Core;
using VDF.Core.Scheduling;
using VDF.Core.Utils;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels;

public sealed record ScheduleKindOption(ScanScheduleKind Value, string Name);
public sealed record ScheduledProfileOption(ScanProfile Value, string Name);

public partial class MainWindowVM {
	readonly DispatcherTimer scheduledScanTimer = new();
	readonly ScanRunGate scanRunGate = new();
	bool scheduledScanningReady;
	bool scheduleCheckInProgress;
	bool scheduledScanInProgress;
	bool automationScanLaunched;
	bool scheduleSettingsDirty;

	public bool HasActiveScanRun => scanRunGate.IsActive;
	public ScheduleKindOption[] ScheduleKindOptions { get; } = [
		new(ScanScheduleKind.Daily, "Daily"), new(ScanScheduleKind.Weekly, "Weekly"),
		new(ScanScheduleKind.Interval, "Periodic reconciliation")
	];
	public ScheduledProfileOption[] ScheduledProfileOptions { get; } = [
		new(ScanProfile.Custom, "Use current scan settings"),
		new(ScanProfile.ExactAndNear, "Exact and near duplicates"),
		new(ScanProfile.EditedAndAltered, "Edited and altered copies"),
		new(ScanProfile.AiScan, "AI scan"), new(ScanProfile.DeepClean, "Deep clean")
	];
	public ScheduleKindOption? SelectedScheduleKind {
		get => ScheduleKindOptions.FirstOrDefault(x => x.Value == SettingsFile.Instance.ScheduledScanKind);
		set { if (value != null) SettingsFile.Instance.ScheduledScanKind = value.Value; }
	}
	public ScheduledProfileOption? SelectedScheduledProfile {
		get => ScheduledProfileOptions.FirstOrDefault(x => x.Value == SettingsFile.Instance.ScheduledScanProfile);
		set { if (value != null) SettingsFile.Instance.ScheduledScanProfile = value.Value; }
	}
	public bool ScheduleUsesCalendar => SettingsFile.Instance.ScheduledScanKind != ScanScheduleKind.Interval;
	public bool ScheduleUsesInterval => !ScheduleUsesCalendar;
	public bool ScheduleUsesWeekdays => SettingsFile.Instance.ScheduledScanKind == ScanScheduleKind.Weekly;
	public string ScheduledTimezoneText => $"Timezone: {TimeZoneInfo.Local.Id}";
	public string ScheduledLastScanText => SettingsFile.Instance.ScheduledLastStartedUtc is { } time
		? $"{time.ToLocalTime():g} — {SettingsFile.Instance.ScheduledLastStatus}"
		: SettingsFile.Instance.ScheduledLastStatus;
	public string ScheduledDurationText {
		get {
			var settings = SettingsFile.Instance;
			double seconds = scheduledScanInProgress && settings.ScheduledLastStartedUtc is { } started
				? Math.Max(0, (DateTimeOffset.UtcNow - started).TotalSeconds) : settings.ScheduledLastDurationSeconds;
			return TimeSpan.FromSeconds(Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, TimeSpan.MaxValue.TotalSeconds - 1)).Format();
		}
	}
	string scheduledNextScanText = "Disabled";
	public string ScheduledNextScanText {
		get => scheduledNextScanText;
		private set => this.RaiseAndSetIfChanged(ref scheduledNextScanText, value);
	}
	public bool ScheduledSunday { get => HasScheduledDay(DayOfWeek.Sunday); set => SetScheduledDay(DayOfWeek.Sunday, value); }
	public bool ScheduledMonday { get => HasScheduledDay(DayOfWeek.Monday); set => SetScheduledDay(DayOfWeek.Monday, value); }
	public bool ScheduledTuesday { get => HasScheduledDay(DayOfWeek.Tuesday); set => SetScheduledDay(DayOfWeek.Tuesday, value); }
	public bool ScheduledWednesday { get => HasScheduledDay(DayOfWeek.Wednesday); set => SetScheduledDay(DayOfWeek.Wednesday, value); }
	public bool ScheduledThursday { get => HasScheduledDay(DayOfWeek.Thursday); set => SetScheduledDay(DayOfWeek.Thursday, value); }
	public bool ScheduledFriday { get => HasScheduledDay(DayOfWeek.Friday); set => SetScheduledDay(DayOfWeek.Friday, value); }
	public bool ScheduledSaturday { get => HasScheduledDay(DayOfWeek.Saturday); set => SetScheduledDay(DayOfWeek.Saturday, value); }
	bool HasScheduledDay(DayOfWeek day) => (SettingsFile.Instance.ScheduledScanWeekdays & (1 << (int)day)) != 0;
	void SetScheduledDay(DayOfWeek day, bool enabled) {
		int mask = 1 << (int)day;
		SettingsFile.Instance.ScheduledScanWeekdays = enabled
			? SettingsFile.Instance.ScheduledScanWeekdays | mask : SettingsFile.Instance.ScheduledScanWeekdays & ~mask;
	}

	void SetupScheduledScanning() {
		// This is the existing GUI scheduler timer. The desktop/container process owns it;
		// no browser connection or additional service is needed.
		scheduledScanTimer.Interval = TimeSpan.FromSeconds(15);
		scheduledScanTimer.Tick += async (_, _) => await CheckScheduledScanAsync();
		SettingsFile.Instance.PropertyChanged += ScheduleSettingsChanged;
		RefreshScheduledScanSummary();
	}

	/// <summary>Call only after the database AND saved results/review state have finished loading.</summary>
	public void InitializeScheduledScanning() {
		if (scheduledScanningReady) return;
		scheduledScanningReady = true;
		if (SettingsFile.Instance.ScheduledLastStatus is "Starting" or "Running") {
			SettingsFile.Instance.ScheduledLastStatus = "Interrupted by application restart";
			SettingsFile.Instance.ScheduledLastCompletedUtc = DateTimeOffset.UtcNow;
			TrySaveScheduledState();
		}
		scheduledScanTimer.Start();
		_ = CheckScheduledScanAsync();
	}

	void ScheduleSettingsChanged(object? sender, PropertyChangedEventArgs e) {
		if (e.PropertyName is nameof(SettingsFile.EnableScheduledScan) or nameof(SettingsFile.ScheduledScanTime)
			or nameof(SettingsFile.ScheduledScanKind) or nameof(SettingsFile.ScheduledScanWeekdays)
			or nameof(SettingsFile.ScheduledScanIntervalMinutes) or nameof(SettingsFile.ScheduledScanCatchUpHours)
			or nameof(SettingsFile.ScheduledScanProfile) or nameof(SettingsFile.NotifyOnScheduledScanComplete)) {
			scheduleSettingsDirty = true;
			this.RaisePropertyChanged(nameof(SelectedScheduleKind));
			this.RaisePropertyChanged(nameof(SelectedScheduledProfile));
			this.RaisePropertyChanged(nameof(ScheduleUsesCalendar));
			this.RaisePropertyChanged(nameof(ScheduleUsesInterval));
			this.RaisePropertyChanged(nameof(ScheduleUsesWeekdays));
			foreach (var name in new[] { nameof(ScheduledSunday), nameof(ScheduledMonday), nameof(ScheduledTuesday),
				nameof(ScheduledWednesday), nameof(ScheduledThursday), nameof(ScheduledFriday), nameof(ScheduledSaturday) })
				this.RaisePropertyChanged(name);
			RefreshScheduledScanSummary();
		}
	}

	ScanScheduleEvaluation EvaluateSchedule(DateTimeOffset now) {
		var s = SettingsFile.Instance;
		return ScanSchedulePolicy.Evaluate(new ScanSchedule {
			Enabled = s.EnableScheduledScan, Kind = s.ScheduledScanKind, LocalTime = s.ScheduledScanTime,
			Weekdays = s.ScheduledScanWeekdays, IntervalMinutes = s.ScheduledScanIntervalMinutes,
			CatchUpHours = s.ScheduledScanCatchUpHours
		}, now, s.ScheduledLastClaimedSlotUtc, TimeZoneInfo.Local);
	}
	void RefreshScheduledScanSummary() {
		var evaluation = EvaluateSchedule(DateTimeOffset.UtcNow);
		ScheduledNextScanText = evaluation.Error ?? (!SettingsFile.Instance.EnableScheduledScan ? "Disabled"
			: evaluation.DueSlotUtc != null && !scheduledScanInProgress ? "Due — waiting until application is idle"
			: evaluation.NextSlotUtc is { } next ? next.ToLocalTime().ToString("g") : "No upcoming scan");
		this.RaisePropertyChanged(nameof(ScheduledLastScanText));
		this.RaisePropertyChanged(nameof(ScheduledDurationText));
	}

	async Task CheckScheduledScanAsync() {
		if (!scheduledScanningReady || scheduleCheckInProgress) return;
		scheduleCheckInProgress = true;
		try {
			if (scheduleSettingsDirty && TrySaveScheduledState()) scheduleSettingsDirty = false;
			RefreshScheduledScanSummary();
			if (EvaluateSchedule(DateTimeOffset.UtcNow).DueSlotUtc is { } slot)
				await TryStartScheduledScanAsync(slot);
		}
		catch (Exception) {
			Logger.Instance.Error("Scheduled scan could not start; check the scan log and configuration storage.");
			if (scheduledScanInProgress) CompleteScheduledScan("Failed to start");
		}
		finally { scheduleCheckInProgress = false; }
	}

	public ReactiveCommand<Unit, Unit> RunScheduledScanNowCommand => ReactiveCommand.CreateFromTask(async () => {
		if (scheduleCheckInProgress) return;
		scheduleCheckInProgress = true;
		try { await TryStartScheduledScanAsync(EvaluateSchedule(DateTimeOffset.UtcNow).DueSlotUtc); }
		finally { scheduleCheckInProgress = false; }
	}, CanStartScan);

	async Task TryStartScheduledScanAsync(DateTimeOffset? slot) {
		if (!scheduledScanningReady || IsStartupLoading || IsScanning || IsBusy || IsAutomationWorking || ShowThumbnailRetrievalProgressBar || scanRunGate.IsActive) return;
		var s = SettingsFile.Instance;
		var previousClaim = s.ScheduledLastClaimedSlotUtc;
		var previousStarted = s.ScheduledLastStartedUtc;
		var previousCompleted = s.ScheduledLastCompletedUtc;
		var previousDuration = s.ScheduledLastDurationSeconds;
		s.ScheduledLastClaimedSlotUtc = slot ?? previousClaim;
		s.ScheduledLastStartedUtc = DateTimeOffset.UtcNow;
		s.ScheduledLastCompletedUtc = null;
		s.ScheduledLastDurationSeconds = 0;
		s.ScheduledLastStatus = "Starting";
		// No work, including replacing results, is permitted before this durable claim.
		if (!TrySaveScheduledState()) {
			s.ScheduledLastClaimedSlotUtc = previousClaim;
			s.ScheduledLastStartedUtc = previousStarted;
			s.ScheduledLastCompletedUtc = previousCompleted;
			s.ScheduledLastDurationSeconds = previousDuration;
			s.ScheduledLastStatus = "Not started: schedule state could not be saved";
			RefreshScheduledScanSummary();
			return;
		}
		scheduledScanInProgress = true;
		automationScanLaunched = false;
		try { await StartScanCommand.Execute("ScheduledFullScan").ToTask(); }
		catch (Exception) { CompleteScheduledScan("Failed to start"); }
		finally {
			if (!automationScanLaunched && scheduledScanInProgress) CompleteScheduledScan(s.ScheduledLastStatus);
			RefreshScheduledScanSummary();
		}
	}

	bool TrySaveScheduledState() {
		try { SettingsFile.SaveSettings(); return true; }
		catch (Exception) {
			Logger.Instance.Error("Schedule state could not be saved; check configuration storage and permissions.");
			return false;
		}
	}

	void CompleteScheduledScan(string status) {
		if (scheduledScanInProgress) {
			var s = SettingsFile.Instance;
			s.ScheduledLastStatus = status;
			s.ScheduledLastCompletedUtc = DateTimeOffset.UtcNow;
			s.ScheduledLastDurationSeconds = s.ScheduledLastStartedUtc is { } started
				? Math.Max(0, (s.ScheduledLastCompletedUtc.Value - started).TotalSeconds) : 0;
			scheduledScanInProgress = false;
			automationScanLaunched = false;
			TrySaveScheduledState();
		}
		scanRunGate.Complete();
		this.RaisePropertyChanged(nameof(HasActiveScanRun));
		RefreshScheduledScanSummary();
	}

	string? ValidateScheduledScanPrerequisites() {
		var s = SettingsFile.Instance;
		if (!Enum.IsDefined(s.ScheduledScanProfile)) return "Not started: choose a valid scan profile";
		if (!string.IsNullOrEmpty(s.CustomDatabaseFolder) && !System.IO.Directory.Exists(s.CustomDatabaseFolder))
			return "Not started: configured database folder is unavailable";
		if ((s.UseNativeFfmpegBinding && !ScanEngine.NativeFFmpegExists) || (!s.UseNativeFfmpegBinding && !ScanEngine.FFmpegExists))
			return "Not started: FFmpeg is missing";
		if (!ScanEngine.FFprobeExists) return "Not started: FFprobe is missing";
		if (s.UseNativeFfmpegBinding && s.HardwareAccelerationMode == Core.FFTools.FFHardwareAccelerationMode.auto)
			return "Not started: native FFmpeg does not support automatic hardware acceleration";
		if (s.NeedsAiComponents && !Core.AI.AiComponents.IsReady) return "Not started: AI components are missing";
		if (s.Includes.Count == 0) return "Not started: no scan folders are configured";
		if (s.Includes.Any(folder => !System.IO.Directory.Exists(folder))) return "Not started: an included folder is unavailable";
		if (s.MaxDegreeOfParallelism == 0) return "Not started: scan concurrency must be nonzero";
		if (s.FilterByFileSize && s.MaximumFileSize <= s.MinimumFileSize) return "Not started: file size filters are invalid";
		return null;
	}
}
