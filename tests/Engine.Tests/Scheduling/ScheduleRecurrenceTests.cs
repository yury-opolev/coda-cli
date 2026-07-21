using Coda.Agent.Scheduling;

namespace Engine.Tests.Scheduling;

public sealed class ScheduleRecurrenceTests
{
    private static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone(
        "Test/PlusTwo", TimeSpan.FromHours(2), "Test/PlusTwo", "Test/PlusTwo");

    private static TimeZoneInfo BuildDstZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone(
            "Test/DstPlusOne", TimeSpan.FromHours(1), "Test/DstPlusOne", "Test/DstPlusOne",
            "Test/DstPlusOne (DST)", [rule]);
    }

    private static ScheduledTask IntervalDefinition(
        TimeSpan interval, DateTimeOffset nextRunUtc) =>
        new(
            ScheduledTask.CurrentSchemaVersion, "s1", null, ScheduleKind.Interval, "check",
            interval, null, null, "UTC",
            nextRunUtc,
            DateTimeOffset.Parse("2026-07-21T08:00:00Z"),
            DateTimeOffset.Parse("2026-07-21T08:00:00Z"),
            null);

    [Fact]
    public void AdvanceInterval_uses_previous_boundary_and_skips_missed_ticks()
    {
        var definition = IntervalDefinition(
            TimeSpan.FromMinutes(3), DateTimeOffset.Parse("2026-07-21T08:03:00Z"));

        var next = ScheduleRecurrence.AdvanceRecurringPast(
            definition, DateTimeOffset.Parse("2026-07-21T08:10:30Z"));

        Assert.Equal(DateTimeOffset.Parse("2026-07-21T08:12:00Z"), next);
    }

    [Fact]
    public void AdvanceInterval_returns_future_boundary_unchanged_when_not_yet_due()
    {
        var definition = IntervalDefinition(
            TimeSpan.FromMinutes(3), DateTimeOffset.Parse("2026-07-21T08:12:00Z"));

        var next = ScheduleRecurrence.AdvanceRecurringPast(
            definition, DateTimeOffset.Parse("2026-07-21T08:10:30Z"));

        Assert.Equal(DateTimeOffset.Parse("2026-07-21T08:12:00Z"), next);
    }

    [Fact]
    public void NextCronOccurrence_uses_definition_timezone()
    {
        Assert.True(CronExpression.TryParse("0 9 * * *", out var cron, out _));

        var next = ScheduleRecurrence.GetNextCronOccurrence(
            cron!, DateTimeOffset.Parse("2026-07-21T08:30:00Z"), PlusTwo);

        // 09:00 local in a +02:00 zone has already passed (10:30 local) => next day 07:00 UTC.
        Assert.Equal(DateTimeOffset.Parse("2026-07-22T07:00:00Z"), next);
    }

    [Fact]
    public void NextCronOccurrence_skips_spring_forward_missing_local_time()
    {
        Assert.True(CronExpression.TryParse("30 2 * * *", out var cron, out _));
        var zone = BuildDstZone();

        var next = ScheduleRecurrence.GetNextCronOccurrence(
            cron!, DateTimeOffset.Parse("2026-03-29T00:00:00Z"), zone);

        // 2026-03-29 02:30 local does not exist (spring-forward) => next is 2026-03-30 02:30 local,
        // which under daylight (+02:00) is 00:30 UTC.
        Assert.Equal(DateTimeOffset.Parse("2026-03-30T00:30:00Z"), next);
    }

    [Fact]
    public void NextCronOccurrence_fall_back_fires_once_at_earlier_utc()
    {
        Assert.True(CronExpression.TryParse("30 2 * * *", out var cron, out _));
        var zone = BuildDstZone();

        var first = ScheduleRecurrence.GetNextCronOccurrence(
            cron!, DateTimeOffset.Parse("2026-10-24T12:00:00Z"), zone);
        // Ambiguous 02:30 local => earlier UTC instant (larger, +02:00 offset).
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:30:00Z"), first);

        var second = ScheduleRecurrence.GetNextCronOccurrence(cron!, first, zone);
        // The repeated 02:30 (standard +01:00 => 01:30 UTC) must NOT fire; the next run is the
        // following day's 02:30 local (+01:00 => 01:30 UTC on 2026-10-26).
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T01:30:00Z"), second);
    }

    [Fact]
    public void NextCronOccurrence_supports_leap_day_schedule()
    {
        Assert.True(CronExpression.TryParse("0 0 29 2 *", out var cron, out _));

        var next = ScheduleRecurrence.GetNextCronOccurrence(
            cron!, DateTimeOffset.Parse("2025-03-01T00:00:00Z"), TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2028-02-29T00:00:00Z"), next);
    }

    [Fact]
    public void NextCronOccurrence_supports_leap_day_across_century_gap()
    {
        Assert.True(CronExpression.TryParse("0 0 29 2 *", out var cron, out _));

        // 2100 is not a leap year, so the next Feb 29 after 2096 is 2104 — an eight-year gap.
        var next = ScheduleRecurrence.GetNextCronOccurrence(
            cron!, DateTimeOffset.Parse("2096-03-01T00:00:00Z"), TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse("2104-02-29T00:00:00Z"), next);
    }

    [Fact]
    public void NextCronOccurrence_throws_for_impossible_date()
    {
        Assert.True(CronExpression.TryParse("0 0 30 2 *", out var cron, out _));

        Assert.Throws<InvalidOperationException>(() => ScheduleRecurrence.GetNextCronOccurrence(
            cron!, DateTimeOffset.Parse("2025-01-01T00:00:00Z"), TimeZoneInfo.Utc));
    }
}
