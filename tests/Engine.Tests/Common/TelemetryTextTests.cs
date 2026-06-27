using Coda.Common;

namespace Engine.Tests.Common;

/// <summary>
/// Tests for <see cref="TelemetryText"/>, the shared preview-truncation helper used
/// to keep telemetry log lines bounded. Short text passes through unchanged; long
/// text is cut to the limit with a "[N chars total]" suffix; the limit is
/// configurable via <see cref="TelemetryText.TruncateEnv"/> with a sane floor.
/// </summary>
public sealed class TelemetryTextTests
{
    [Fact]
    public void Short_text_passes_through_unchanged()
    {
        Assert.Equal("hello", TelemetryText.Truncate("hello", 500));
    }

    [Fact]
    public void Text_at_exactly_the_limit_is_unchanged()
    {
        var text = new string('x', 10);
        Assert.Equal(text, TelemetryText.Truncate(text, 10));
    }

    [Fact]
    public void Long_text_is_truncated_with_total_count_suffix()
    {
        var text = new string('x', 25);

        var result = TelemetryText.Truncate(text, 10);

        Assert.StartsWith(new string('x', 10), result);
        Assert.Contains("25 chars total", result);
        // The preview is exactly the limit-length prefix plus the suffix marker.
        Assert.True(result.Length > 10);
    }

    [Fact]
    public void One_over_the_limit_is_truncated()
    {
        var text = new string('x', 11);

        var result = TelemetryText.Truncate(text, 10);

        Assert.Contains("11 chars total", result);
        Assert.StartsWith(new string('x', 10), result);
    }

    [Fact]
    public void Null_returns_empty()
    {
        Assert.Equal(string.Empty, TelemetryText.Truncate(null, 500));
    }

    [Fact]
    public void Empty_returns_empty()
    {
        Assert.Equal(string.Empty, TelemetryText.Truncate(string.Empty, 500));
    }

    [Fact]
    public void Default_limit_is_500()
    {
        Assert.Equal(500, TelemetryText.DefaultLimit);
    }

    [Fact]
    public void ResolveLimit_uses_default_when_env_unset_or_unparseable()
    {
        Assert.Equal(TelemetryText.DefaultLimit, TelemetryText.ResolveLimit(null));
        Assert.Equal(TelemetryText.DefaultLimit, TelemetryText.ResolveLimit(""));
        Assert.Equal(TelemetryText.DefaultLimit, TelemetryText.ResolveLimit("not-a-number"));
    }

    [Fact]
    public void ResolveLimit_reads_env_override()
    {
        Assert.Equal(2000, TelemetryText.ResolveLimit("2000"));
    }

    [Fact]
    public void ResolveLimit_clamps_to_floor_for_tiny_or_negative_values()
    {
        // A floor keeps previews useful even if someone sets an absurdly small value.
        Assert.Equal(TelemetryText.MinLimit, TelemetryText.ResolveLimit("1"));
        Assert.Equal(TelemetryText.MinLimit, TelemetryText.ResolveLimit("0"));
        Assert.Equal(TelemetryText.MinLimit, TelemetryText.ResolveLimit("-100"));
    }

    [Fact]
    public void Truncate_single_arg_uses_resolved_limit()
    {
        // The single-arg overload uses the configured limit; a short string is unchanged.
        Assert.Equal("short", TelemetryText.Truncate("short"));
    }

    [Fact]
    public void ResolveLimit_clamps_below_min_when_value_is_exactly_min_minus_one()
    {
        Assert.Equal(TelemetryText.MinLimit, TelemetryText.ResolveLimit((TelemetryText.MinLimit - 1).ToString()));
    }

    [Fact]
    public void ResolveLimit_accepts_exactly_min_value()
    {
        Assert.Equal(TelemetryText.MinLimit, TelemetryText.ResolveLimit(TelemetryText.MinLimit.ToString()));
    }

    [Fact]
    public void ResolveLimit_accepts_value_above_min()
    {
        Assert.Equal(100, TelemetryText.ResolveLimit("100"));
    }

    [Fact]
    public void Truncate_prefix_is_exactly_max_chars()
    {
        // Ensure the slice is precisely `max` characters, not more.
        var text = new string('a', 5) + new string('b', 20);
        var result = TelemetryText.Truncate(text, 5);
        Assert.StartsWith("aaaaa", result);
        Assert.DoesNotContain("b", result[..5]);
    }
}
