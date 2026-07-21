using Coda.Agent.Scheduling;

namespace Engine.Tests.Scheduling;

/// <summary>
/// Verifies <see cref="ScheduleTimeZones.TryResolve"/> rejects malformed or out-of-range
/// fixed-offset ids without throwing.
/// </summary>
public sealed class ScheduleTimeZonesTests
{
    [Theory]
    [InlineData("UTC+15:00")]
    [InlineData("UTC+99:99")]
    public void TryResolve_rejects_out_of_range_fixed_offset(string id)
    {
        var exception = Record.Exception(() =>
        {
            Assert.False(ScheduleTimeZones.TryResolve(id, out var zone));
            Assert.Null(zone);
        });

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("UTC+15:00")]
    [InlineData("UTC+99:99")]
    public void ParseCron_surfaces_clear_error_for_invalid_fixed_offset(string id)
    {
        var request = new ScheduleCreateRequest(null, "cron", null, null, "0 9 * * *", id);

        Assert.False(ScheduleDefinitionParser.TryParse(
            request,
            DateTimeOffset.Parse("2026-07-21T08:00:00Z"),
            TimeZoneInfo.Utc,
            out _,
            out var error));
        Assert.NotNull(error);
        Assert.Contains(id, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("UTC+02:00")]
    [InlineData("UTC-05:30")]
    [InlineData("UTC+14:00")]
    public void TryResolve_accepts_valid_fixed_offsets(string id)
    {
        Assert.True(ScheduleTimeZones.TryResolve(id, out var zone));
        Assert.NotNull(zone);
    }
}
