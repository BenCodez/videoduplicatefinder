using System.Text.Json;
using VDF.Core.Scheduling;
using VDF.GUI.Data;

namespace VDF.GUI.Tests;

public class ScheduledScanSettingsTests {
	[Fact]
	public void LegacySchedulerSettings_RetainDailyDefaultsAndExistingToggles() {
		var restored = JsonSerializer.Deserialize("""
			{"EnableScheduledScan":true,"ScheduledScanTime":"03:15","NotifyOnScheduledScanComplete":false}
			""", GuiJsonContext.Default.SettingsFile)!;
		Assert.True(restored.EnableScheduledScan);
		Assert.Equal("03:15", restored.ScheduledScanTime);
		Assert.False(restored.NotifyOnScheduledScanComplete);
		Assert.Equal(ScanScheduleKind.Daily, restored.ScheduledScanKind);
		Assert.Equal(ScanProfile.Custom, restored.ScheduledScanProfile);
		Assert.Null(restored.ScheduledLastClaimedSlotUtc);
		Assert.Equal(24, restored.ScheduledScanCatchUpHours);
	}

	[Fact]
	public void SourceGeneratedSerialization_RoundTripsScheduleAndDurableClaim() {
		var claim = new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero);
		var settings = new SettingsFile {
			EnableScheduledScan = true, ScheduledScanKind = ScanScheduleKind.Weekly,
			ScheduledScanTime = "06:30", ScheduledScanWeekdays = 42, ScheduledScanIntervalMinutes = 180,
			ScheduledScanCatchUpHours = 48, ScheduledScanProfile = ScanProfile.ExactAndNear,
			ScheduledLastClaimedSlotUtc = claim, ScheduledLastStartedUtc = claim.AddMinutes(1),
			ScheduledLastCompletedUtc = claim.AddMinutes(5), ScheduledLastDurationSeconds = 240,
			ScheduledLastStatus = "Completed"
		};
		var json = JsonSerializer.Serialize(settings, GuiJsonContext.Default.SettingsFile);
		var restored = JsonSerializer.Deserialize(json, GuiJsonContext.Default.SettingsFile)!;
		Assert.Equal(settings.ScheduledScanKind, restored.ScheduledScanKind);
		Assert.Equal(settings.ScheduledScanWeekdays, restored.ScheduledScanWeekdays);
		Assert.Equal(settings.ScheduledScanIntervalMinutes, restored.ScheduledScanIntervalMinutes);
		Assert.Equal(settings.ScheduledScanCatchUpHours, restored.ScheduledScanCatchUpHours);
		Assert.Equal(settings.ScheduledScanProfile, restored.ScheduledScanProfile);
		Assert.Equal(settings.ScheduledLastClaimedSlotUtc, restored.ScheduledLastClaimedSlotUtc);
		Assert.Equal(settings.ScheduledLastStartedUtc, restored.ScheduledLastStartedUtc);
		Assert.Equal(settings.ScheduledLastCompletedUtc, restored.ScheduledLastCompletedUtc);
		Assert.Equal(settings.ScheduledLastDurationSeconds, restored.ScheduledLastDurationSeconds);
		Assert.Equal(settings.ScheduledLastStatus, restored.ScheduledLastStatus);
	}
}
