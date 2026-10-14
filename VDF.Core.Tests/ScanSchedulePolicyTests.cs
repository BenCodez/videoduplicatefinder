using System.Globalization;
using VDF.Core.Scheduling;

namespace VDF.Core.Tests;

public class ScanSchedulePolicyTests {
	static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();
	static readonly ScanSchedule Daily = new() { Enabled = true, LocalTime = "02:00" };

	[Fact]
	public void Daily_ClaimsDueSlotOnceAcrossRestartOrFailure() {
		var now = Utc("2026-10-13T02:01:00Z");
		var initial = ScanSchedulePolicy.Evaluate(Daily, now, null, TimeZoneInfo.Utc);
		Assert.Equal(Utc("2026-10-13T02:00:00Z"), initial.DueSlotUtc);
		Assert.Equal(Utc("2026-10-14T02:00:00Z"), initial.NextSlotUtc);
		// The same persisted watermark is used for successful, cancelled, failed and interrupted jobs.
		var restarted = ScanSchedulePolicy.Evaluate(Daily, now.AddMinutes(10), initial.DueSlotUtc, TimeZoneInfo.Utc);
		Assert.Null(restarted.DueSlotUtc);
		Assert.Equal(initial.NextSlotUtc, restarted.NextSlotUtc);
	}

	[Fact]
	public void Daily_BeforeTodayTime_CatchesUpYesterdayWithinWindow() {
		var result = ScanSchedulePolicy.Evaluate(Daily, Utc("2026-10-13T01:00:00Z"), null, TimeZoneInfo.Utc);
		Assert.Equal(Utc("2026-10-12T02:00:00Z"), result.DueSlotUtc);
		Assert.Equal(Utc("2026-10-13T02:00:00Z"), result.NextSlotUtc);
	}

	[Fact]
	public void OldMissedSlotOutsideWindow_IsNotReplayed() {
		var result = ScanSchedulePolicy.Evaluate(Daily with { CatchUpHours = 1 }, Utc("2026-10-13T09:00:00Z"), null, TimeZoneInfo.Utc);
		Assert.Null(result.DueSlotUtc);
		Assert.Equal(Utc("2026-10-14T02:00:00Z"), result.NextSlotUtc);
	}

	[Fact]
	public void Weekly_UsesSelectedDaysAndCollapsesBacklogToLatest() {
		var weekly = Daily with {
			Kind = ScanScheduleKind.Weekly, Weekdays = (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Wednesday),
			CatchUpHours = 168
		};
		var result = ScanSchedulePolicy.Evaluate(weekly, Utc("2026-10-15T12:00:00Z"), Utc("2026-09-01T02:00:00Z"), TimeZoneInfo.Utc);
		Assert.Equal(Utc("2026-10-14T02:00:00Z"), result.DueSlotUtc);
		Assert.Equal(Utc("2026-10-19T02:00:00Z"), result.NextSlotUtc);
	}

	[Fact]
	public void Weekly_AtExactTime_IsDueAndNextIsStrictlyFuture() {
		var weekly = Daily with { Kind = ScanScheduleKind.Weekly, Weekdays = 1 << (int)DayOfWeek.Tuesday };
		var result = ScanSchedulePolicy.Evaluate(weekly, Utc("2026-10-13T02:00:00Z"), null, TimeZoneInfo.Utc);
		Assert.Equal(Utc("2026-10-13T02:00:00Z"), result.DueSlotUtc);
		Assert.Equal(Utc("2026-10-20T02:00:00Z"), result.NextSlotUtc);
	}

	[Fact]
	public void Interval_HandlesLongDowntimeAsOneLatestRun() {
		var periodic = Daily with { Kind = ScanScheduleKind.Interval, IntervalMinutes = 30 };
		var result = ScanSchedulePolicy.Evaluate(periodic, Utc("2026-10-13T14:42:10Z"), Utc("2026-10-01T00:00:00Z"), TimeZoneInfo.Utc);
		Assert.Equal(Utc("2026-10-13T14:30:00Z"), result.DueSlotUtc);
		Assert.Equal(Utc("2026-10-13T15:00:00Z"), result.NextSlotUtc);
		Assert.Null(ScanSchedulePolicy.Evaluate(periodic, Utc("2026-10-13T14:59:59Z"), result.DueSlotUtc, TimeZoneInfo.Utc).DueSlotUtc);
		Assert.Equal(Utc("2026-10-13T15:00:00Z"), ScanSchedulePolicy.Evaluate(periodic,
			Utc("2026-10-13T15:00:00Z"), result.DueSlotUtc, TimeZoneInfo.Utc).DueSlotUtc);
	}

	[Fact]
	public void BusyRunThatCrossesSeveralIntervals_ProducesOnePendingJob() {
		var periodic = Daily with { Kind = ScanScheduleKind.Interval, IntervalMinutes = 15 };
		var result = ScanSchedulePolicy.Evaluate(periodic, Utc("2026-10-13T05:04:00Z"), Utc("2026-10-13T02:00:00Z"), TimeZoneInfo.Utc);
		Assert.Equal(Utc("2026-10-13T05:00:00Z"), result.DueSlotUtc);
		Assert.Null(ScanSchedulePolicy.Evaluate(periodic, Utc("2026-10-13T05:05:00Z"), result.DueSlotUtc, TimeZoneInfo.Utc).DueSlotUtc);
	}

	[Fact]
	public void ClockMovingBackward_DoesNotRepeatAClaimedJob() {
		var claimed = Utc("2026-10-13T02:00:00Z");
		var result = ScanSchedulePolicy.Evaluate(Daily, Utc("2026-10-12T03:00:00Z"), claimed, TimeZoneInfo.Utc);
		Assert.Null(result.DueSlotUtc);
		Assert.Equal(Utc("2026-10-14T02:00:00Z"), result.NextSlotUtc);
	}

	[Fact]
	public void HalfHourTimezone_UsesLocalCalendarDay() {
		var zone = TimeZoneInfo.CreateCustomTimeZone("test-plus0530", TimeSpan.FromMinutes(330), "test", "test");
		var result = ScanSchedulePolicy.Evaluate(Daily, Utc("2026-10-12T20:31:00Z"), null, zone);
		Assert.Equal(Utc("2026-10-12T20:30:00Z"), result.DueSlotUtc);
		Assert.Equal(Utc("2026-10-13T20:30:00Z"), result.NextSlotUtc);
	}

	static TimeZoneInfo DstZone() {
		var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31),
			TimeSpan.FromHours(1),
			TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
			TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday));
		return TimeZoneInfo.CreateCustomTimeZone("test-dst", TimeSpan.FromHours(-5), "test", "standard", "daylight", [rule]);
	}

	[Fact]
	public void SpringForward_MissingTimeRunsAtFirstValidMinute() {
		var result = ScanSchedulePolicy.Evaluate(Daily with { LocalTime = "02:30" }, Utc("2026-03-08T07:02:00Z"), null, DstZone());
		Assert.Equal(Utc("2026-03-08T07:00:00Z"), result.DueSlotUtc);
		Assert.Equal(Utc("2026-03-09T06:30:00Z"), result.NextSlotUtc);
	}

	[Fact]
	public void FallBack_AmbiguousTimeHasOneEarlierSlot() {
		var schedule = Daily with { LocalTime = "01:30" };
		var result = ScanSchedulePolicy.Evaluate(schedule, Utc("2026-11-01T06:45:00Z"), null, DstZone());
		Assert.Equal(Utc("2026-11-01T05:30:00Z"), result.DueSlotUtc);
		Assert.Equal(Utc("2026-11-02T06:30:00Z"), result.NextSlotUtc);
		Assert.Null(ScanSchedulePolicy.Evaluate(schedule, Utc("2026-11-01T06:45:00Z"), result.DueSlotUtc, DstZone()).DueSlotUtc);
	}

	[Fact]
	public void Interval_IgnoresWallClockDstRepetition() {
		var result = ScanSchedulePolicy.Evaluate(Daily with { Kind = ScanScheduleKind.Interval, IntervalMinutes = 60 },
			Utc("2026-11-01T06:45:00Z"), Utc("2026-11-01T05:00:00Z"), DstZone());
		Assert.Equal(Utc("2026-11-01T06:00:00Z"), result.DueSlotUtc);
		Assert.Equal(Utc("2026-11-01T07:00:00Z"), result.NextSlotUtc);
	}

	[Theory]
	[InlineData("24:00")]
	[InlineData("-02:00")]
	[InlineData("02:00:30")]
	[InlineData("2.02:00")]
	[InlineData("invalid")]
	[InlineData("")]
	public void InvalidTime_HasVisibleErrorAndNoJob(string value) {
		var result = ScanSchedulePolicy.Evaluate(Daily with { LocalTime = value }, Utc("2026-10-13T12:00:00Z"), null, TimeZoneInfo.Utc);
		Assert.NotNull(result.Error);
		Assert.Null(result.DueSlotUtc);
		Assert.Null(result.NextSlotUtc);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(4)]
	[InlineData(10081)]
	public void InvalidInterval_ProducesNoJobs(int minutes) {
		var result = ScanSchedulePolicy.Evaluate(Daily with { Kind = ScanScheduleKind.Interval, IntervalMinutes = minutes },
			Utc("2026-10-13T12:00:00Z"), null, TimeZoneInfo.Utc);
		Assert.NotNull(result.Error);
		Assert.Null(result.DueSlotUtc);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(169)]
	public void InvalidCatchUpWindow_ProducesNoJobs(int hours) {
		var result = ScanSchedulePolicy.Evaluate(Daily with { CatchUpHours = hours }, Utc("2026-10-13T12:00:00Z"), null, TimeZoneInfo.Utc);
		Assert.NotNull(result.Error);
		Assert.Null(result.DueSlotUtc);
	}

	[Fact]
	public void NoWeeklyDaysOrUnknownScheduleType_ProducesNoJobs() {
		Assert.NotNull(ScanSchedulePolicy.Evaluate(Daily with { Kind = ScanScheduleKind.Weekly, Weekdays = 0 },
			Utc("2026-10-13T12:00:00Z"), null, TimeZoneInfo.Utc).Error);
		Assert.NotNull(ScanSchedulePolicy.Evaluate(Daily with { Kind = (ScanScheduleKind)99 },
			Utc("2026-10-13T12:00:00Z"), null, TimeZoneInfo.Utc).Error);
	}

	[Fact]
	public void DisabledSchedule_NeverProducesJobs() {
		Assert.Equal(new ScanScheduleEvaluation(null, null, null), ScanSchedulePolicy.Evaluate(
			Daily with { Enabled = false, LocalTime = "invalid" }, Utc("2026-10-13T12:00:00Z"), null, TimeZoneInfo.Utc));
	}

	[Fact]
	public void Gate_OnlyOneManualOrScheduledRunCanOwnReservation() {
		var gate = new ScanRunGate();
		int winners = 0;
		Parallel.For(0, 100, _ => { if (gate.TryBegin()) Interlocked.Increment(ref winners); });
		Assert.Equal(1, winners);
		Assert.True(gate.IsActive);
		Assert.False(gate.TryBegin());
		gate.Complete();
		Assert.False(gate.IsActive);
		Assert.True(gate.TryBegin());
		gate.Complete();
	}
}
