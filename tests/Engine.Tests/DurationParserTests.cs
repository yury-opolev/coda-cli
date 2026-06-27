using Coda.Agent.Goals;

namespace Engine.Tests;

public sealed class DurationParserTests
{
    // --- suffix forms ---

    [Fact]
    public void Parses_seconds_suffix()
    {
        Assert.True(DurationParser.TryParse("90s", out var result));
        Assert.Equal(TimeSpan.FromSeconds(90), result);
    }

    [Fact]
    public void Parses_minutes_suffix()
    {
        Assert.True(DurationParser.TryParse("30m", out var result));
        Assert.Equal(TimeSpan.FromMinutes(30), result);
    }

    [Fact]
    public void Parses_hours_suffix()
    {
        Assert.True(DurationParser.TryParse("2h", out var result));
        Assert.Equal(TimeSpan.FromHours(2), result);
    }

    [Fact]
    public void Parses_days_suffix()
    {
        Assert.True(DurationParser.TryParse("1d", out var result));
        Assert.Equal(TimeSpan.FromDays(1), result);
    }

    // --- case insensitivity ---

    [Fact]
    public void Suffix_is_case_insensitive_uppercase_M()
    {
        Assert.True(DurationParser.TryParse("30M", out var result));
        Assert.Equal(TimeSpan.FromMinutes(30), result);
    }

    [Fact]
    public void Suffix_is_case_insensitive_uppercase_H()
    {
        Assert.True(DurationParser.TryParse("2H", out var result));
        Assert.Equal(TimeSpan.FromHours(2), result);
    }

    [Fact]
    public void Suffix_is_case_insensitive_uppercase_S()
    {
        Assert.True(DurationParser.TryParse("90S", out var result));
        Assert.Equal(TimeSpan.FromSeconds(90), result);
    }

    [Fact]
    public void Suffix_is_case_insensitive_uppercase_D()
    {
        Assert.True(DurationParser.TryParse("1D", out var result));
        Assert.Equal(TimeSpan.FromDays(1), result);
    }

    // --- TimeSpan fallback ---

    [Fact]
    public void Parses_hhmmss_format()
    {
        Assert.True(DurationParser.TryParse("01:30:00", out var result));
        Assert.Equal(TimeSpan.FromMinutes(90), result);
    }

    [Fact]
    public void Parses_dd_hhmmss_format()
    {
        Assert.True(DurationParser.TryParse("1.00:00:00", out var result));
        Assert.Equal(TimeSpan.FromDays(1), result);
    }

    // --- invalid inputs ---

    [Fact]
    public void Returns_false_for_null()
    {
        Assert.False(DurationParser.TryParse(null, out _));
    }

    [Fact]
    public void Returns_false_for_empty_string()
    {
        Assert.False(DurationParser.TryParse(string.Empty, out _));
    }

    [Fact]
    public void Returns_false_for_whitespace()
    {
        Assert.False(DurationParser.TryParse("   ", out _));
    }

    [Fact]
    public void Returns_false_for_alphabetic_garbage()
    {
        Assert.False(DurationParser.TryParse("abc", out _));
    }

    [Fact]
    public void Returns_false_for_zero_seconds()
    {
        Assert.False(DurationParser.TryParse("0s", out _));
    }

    [Fact]
    public void Returns_false_for_zero_timespan()
    {
        Assert.False(DurationParser.TryParse("0", out _));
    }

    [Fact]
    public void Returns_false_for_negative_minutes()
    {
        Assert.False(DurationParser.TryParse("-5m", out _));
    }

    [Theory]
    [InlineData("60")]
    [InlineData("1")]
    [InlineData("120")]
    public void Returns_false_for_bare_integer_without_unit_or_colon(string value)
    {
        // "60" must NOT silently parse as 60 days; require a unit suffix or hh:mm:ss.
        Assert.False(DurationParser.TryParse(value, out _));
    }
}
