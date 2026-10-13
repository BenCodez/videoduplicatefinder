using System;
using System.Globalization;

namespace VDF.Core.Scheduling;

public enum ScanScheduleKind { Daily, Weekly, Interval }

/// <summary>Calendar schedules use the process/container's configured timezone. Intervals use UTC.</summary>
public sealed record ScanSchedule {
	public bool Enabled { get; init; }
	public ScanScheduleKind Kind { get; init; }
	public string LocalTime { get; init; } = "02:00";
	/// <summary>Sunday is bit 0, Monday bit 1, through Saturday bit 6.</summary>
	public int Weekdays { get; init; } = 2;
	public int IntervalMinutes { get; init; } = 60;
	/// <summary>Collapse missed slots into at most one recent run; never replay the backlog.</summary>
	public int CatchUpHours { get; init; } = 24;
}

public sealed record ScanScheduleEvaluation(DateTimeOffset? DueSlotUtc, DateTimeOffset? NextSlotUtc, string? Error);

/// <summary>
/// Pure scheduling policy. Callers atomically persist DueSlotUtc BEFORE starting a scan,
/// then pass that claim back after a restart, cancellation or failure. A claimed slot is
/// never retried automatically. Only the newest missed slot within CatchUpHours may run.
/// </summary>
public static class ScanSchedulePolicy {
	public static ScanScheduleEvaluation Evaluate(ScanSchedule schedule, DateTimeOffset now,
		DateTimeOffset? lastClaimedSlotUtc, TimeZoneInfo timezone) {
		if (!schedule.Enabled) return new(null, null, null);
		if (!Enum.IsDefined(schedule.Kind)) return new(null, null, "Choose a valid schedule type.");
		if (schedule.CatchUpHours is < 1 or > 168)
			return new(null, null, "Missed scan window must be from 1 to 168 hours.");
		if (schedule.Kind == ScanScheduleKind.Interval && schedule.IntervalMinutes is < 5 or > 10080)
			return new(null, null, "Scan interval must be from 5 to 10080 minutes.");
		if (schedule.Kind == ScanScheduleKind.Weekly && (schedule.Weekdays & 127) == 0)
			return new(null, null, "Choose at least one weekday.");
		if (schedule.Kind != ScanScheduleKind.Interval && !TryParseTime(schedule.LocalTime, out _))
			return new(null, null, "Use a valid local time in HH:mm format (00:00–23:59).");

		now = now.ToUniversalTime();
		var nextAfter = lastClaimedSlotUtc > now ? lastClaimedSlotUtc.Value : now;
		DateTimeOffset? latest = null;
		DateTimeOffset? next = null;
		if (schedule.Kind == ScanScheduleKind.Interval) {
			long periodTicks = TimeSpan.FromMinutes(schedule.IntervalMinutes).Ticks;
			long sinceEpoch = now.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
			long remainder = ((sinceEpoch % periodTicks) + periodTicks) % periodTicks;
			latest = now.AddTicks(-remainder);
			long nextRemainder = (((nextAfter.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) % periodTicks) + periodTicks) % periodTicks;
			next = nextAfter.AddTicks(periodTicks - nextRemainder);
		}
		else {
			TryParseTime(schedule.LocalTime, out var time);
			var localToday = TimeZoneInfo.ConvertTime(now, timezone).Date;
			for (int day = -7; day <= 0; day++) {
				var date = localToday.AddDays(day);
				if (!IncludesDay(schedule, date.DayOfWeek)) continue;
				var slot = CalendarSlotUtc(date + time, timezone);
				if (slot <= now && (latest == null || slot > latest)) latest = slot;
			}
			var nextLocalDay = TimeZoneInfo.ConvertTime(nextAfter, timezone).Date;
			for (int day = 0; day <= 8; day++) {
				var date = nextLocalDay.AddDays(day);
				if (!IncludesDay(schedule, date.DayOfWeek)) continue;
				var slot = CalendarSlotUtc(date + time, timezone);
				if (slot > nextAfter && (next == null || slot < next)) next = slot;
			}
		}
		var due = latest.HasValue && (!lastClaimedSlotUtc.HasValue || latest > lastClaimedSlotUtc)
			&& now - latest.Value <= TimeSpan.FromHours(schedule.CatchUpHours) ? latest : null;
		return new(due, next, null);
	}

	public static bool TryParseTime(string? value, out TimeSpan time) =>
		TimeSpan.TryParseExact(value, [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out time)
		&& time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

	static bool IncludesDay(ScanSchedule schedule, DayOfWeek day) =>
		schedule.Kind == ScanScheduleKind.Daily || (schedule.Weekdays & (1 << (int)day)) != 0;

	static DateTimeOffset CalendarSlotUtc(DateTime localTime, TimeZoneInfo timezone) {
		localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
		// Spring forward: run at the first valid minute after a missing wall-clock time.
		// Fall back: choose the earlier instant once, rather than producing two jobs.
		for (int minute = 0; timezone.IsInvalidTime(localTime) && minute < 1440; minute++)
			localTime = localTime.AddMinutes(1);
		var offset = timezone.GetUtcOffset(localTime);
		if (timezone.IsAmbiguousTime(localTime)) {
			var offsets = timezone.GetAmbiguousTimeOffsets(localTime);
			offset = offsets[0] > offsets[1] ? offsets[0] : offsets[1];
		}
		return new DateTimeOffset(localTime, offset).ToUniversalTime();
	}
}
