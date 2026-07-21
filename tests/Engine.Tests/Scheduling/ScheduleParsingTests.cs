using Coda.Agent.Scheduling;

namespace Engine.Tests.Scheduling;

public sealed class ScheduleParsingTests
{
    private static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone(
        "Test/PlusTwo", TimeSpan.FromHours(2), "Test/PlusTwo", "Test/PlusTwo");

    // A Central-European-style zone: +01:00 standard, +02:00 daylight, spring-forward on the
    // last Sunday of March at 02:00 and fall-back on the last Sunday of October at 03:00.
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

    [Fact]
    public void TryParse_requires_nonblank_prompt()
    {
        var request = new ScheduleCreateRequest(null, "   ", "3m", null, null, null);
        Assert.False(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_requires_exactly_one_selector()
    {
        var none = new ScheduleCreateRequest(null, "check", null, null, null, null);
        var two = new ScheduleCreateRequest(null, "check", "3m", null, "*/3 * * * *", null);

        Assert.False(ScheduleDefinitionParser.TryParse(
            none, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out _, out var noneError));
        Assert.False(ScheduleDefinitionParser.TryParse(
            two, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out _, out var twoError));
        Assert.Contains("exactly one", noneError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly one", twoError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1m", 1)]
    [InlineData("90m", 90)]
    [InlineData("2h", 120)]
    [InlineData("1d", 1440)]
    [InlineData("1D", 1440)]
    public void TryParse_every_normalizes_supported_durations(string text, int minutes)
    {
        var request = new ScheduleCreateRequest("poll", "check", text, null, null, null);

        Assert.True(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out var draft, out var error), error);
        Assert.Equal(ScheduleKind.Interval, draft!.Kind);
        Assert.Equal(TimeSpan.FromMinutes(minutes), draft.Interval);
        Assert.Equal(DateTimeOffset.Parse("2026-07-21T08:00:00Z") + TimeSpan.FromMinutes(minutes), draft.NextRunUtc);
    }

    [Theory]
    [InlineData("0m")]
    [InlineData("30s")]
    [InlineData("1.5h")]
    [InlineData("-3m")]
    [InlineData("20000000d")]
    [InlineData("100000000000000000000d")]
    public void TryParse_every_rejects_invalid_or_subminute_values(string text)
    {
        var request = new ScheduleCreateRequest(null, "check", text, null, null, null);
        Assert.False(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out _, out _));
    }

    [Fact]
    public void TryParse_at_honors_explicit_offset()
    {
        var request = new ScheduleCreateRequest(
            null, "run once", null, "2026-07-21T18:00:00+02:00", null, null);

        Assert.True(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out var draft, out var error), error);
        Assert.Equal(ScheduleKind.At, draft!.Kind);
        Assert.Equal(DateTimeOffset.Parse("2026-07-21T16:00:00Z"), draft.AtUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-21T16:00:00Z"), draft.NextRunUtc);
        Assert.True(ScheduleTimeZones.TryResolve(draft.TimeZoneId, out _));
    }

    [Fact]
    public void TryParse_at_explicit_utc_z()
    {
        var request = new ScheduleCreateRequest(
            null, "run once", null, "2026-07-21T18:00:00Z", null, null);

        Assert.True(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out var draft, out var error), error);
        Assert.Equal(DateTimeOffset.Parse("2026-07-21T18:00:00Z"), draft!.AtUtc);
    }

    [Fact]
    public void TryParse_at_offsetless_uses_injected_local_zone()
    {
        var request = new ScheduleCreateRequest(
            null, "run once", null, "2026-07-21T18:00:00", null, null);

        Assert.True(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out var draft, out var error), error);
        // 18:00 in a +02:00 zone is 16:00 UTC.
        Assert.Equal(DateTimeOffset.Parse("2026-07-21T16:00:00Z"), draft!.AtUtc);
        Assert.Equal(PlusTwo.Id, draft.TimeZoneId);
    }

    [Fact]
    public void TryParse_at_past_time_is_allowed()
    {
        var request = new ScheduleCreateRequest(
            null, "overdue", null, "2026-07-21T06:00:00Z", null, null);

        Assert.True(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out var draft, out var error), error);
        Assert.Equal(DateTimeOffset.Parse("2026-07-21T06:00:00Z"), draft!.AtUtc);
    }

    [Fact]
    public void TryParse_at_rejects_spring_forward_gap()
    {
        var zone = BuildDstZone();
        var request = new ScheduleCreateRequest(
            null, "gap", null, "2026-03-29T02:30:00", null, null);

        Assert.False(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-03-01T00:00:00Z"), zone, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_at_fall_back_ambiguous_chooses_earlier_utc()
    {
        var zone = BuildDstZone();
        var request = new ScheduleCreateRequest(
            null, "ambiguous", null, "2026-10-25T02:30:00", null, null);

        Assert.True(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-10-01T00:00:00Z"), zone, out var draft, out var error), error);
        // Larger offset (+02:00) => earlier UTC instant.
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:30:00Z"), draft!.AtUtc);
    }

    [Fact]
    public void TryParse_cron_normalizes_whitespace()
    {
        var request = new ScheduleCreateRequest(
            null, "cron", null, null, "*/15    8-18  *  *  1-5", null);

        Assert.True(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out var draft, out var error), error);
        Assert.Equal(ScheduleKind.Cron, draft!.Kind);
        Assert.Equal("*/15 8-18 * * 1-5", draft.Cron);
        Assert.Equal(PlusTwo.Id, draft.TimeZoneId);
    }

    [Fact]
    public void TryParse_cron_rejects_unknown_timezone()
    {
        var request = new ScheduleCreateRequest(
            null, "cron", null, null, "0 9 * * *", "Not/ARealZone");

        Assert.False(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Cron_uses_standard_day_of_month_or_day_of_week_semantics()
    {
        // 13th of the month OR any Friday.
        Assert.True(CronExpression.TryParse("0 0 13 * 5", out var cron, out _));

        // 2026-01-13 is a Tuesday: day-of-month matches, day-of-week does not => OR match.
        Assert.True(cron!.Matches(new DateTime(2026, 1, 13, 0, 0, 0)));
        // 2026-01-16 is a Friday: day-of-week matches, day-of-month does not => OR match.
        Assert.True(cron.Matches(new DateTime(2026, 1, 16, 0, 0, 0)));
        // 2026-01-14 is a Wednesday and not the 13th => neither matches.
        Assert.False(cron.Matches(new DateTime(2026, 1, 14, 0, 0, 0)));
    }

    [Fact]
    public void Cron_single_restricted_day_field_controls()
    {
        // Only day-of-month restricted: matches the 13th regardless of weekday.
        Assert.True(CronExpression.TryParse("0 0 13 * *", out var cron, out _));
        Assert.True(cron!.Matches(new DateTime(2026, 1, 13, 0, 0, 0)));
        Assert.False(cron.Matches(new DateTime(2026, 1, 16, 0, 0, 0)));
    }
}
