using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// <see cref="McpConnectTimeout"/> is the single source of truth for the MCP connection
/// timeout: it resolves the <c>CODA_MCP_CONNECT_TIMEOUT</c> environment value and normalizes
/// any duration against the runtime <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
/// limit so wiring a manager can never make CancelAfter throw.
/// </summary>
public sealed class McpConnectTimeoutTests
{
    /// <summary>The .NET CancelAfter(TimeSpan) limit: uint.MaxValue - 1 milliseconds.</summary>
    private const long MaxSupportedMs = uint.MaxValue - 1;

    [Fact]
    public void Default_is_sixty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), McpConnectTimeout.Default);
        Assert.Equal("CODA_MCP_CONNECT_TIMEOUT", McpConnectTimeout.EnvironmentVariable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    public void Missing_blank_or_invalid_uses_the_default(string? raw)
    {
        Assert.Equal(McpConnectTimeout.Default, McpConnectTimeout.Resolve(raw));
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("1", 1)]
    [InlineData("120", 120)]
    public void Positive_whole_seconds_are_accepted(string raw, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), McpConnectTimeout.Resolve(raw));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-3600")]
    public void Zero_and_negative_become_infinite(string raw)
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, McpConnectTimeout.Resolve(raw));
    }

    [Fact]
    public void Largest_duration_accepted_by_cancel_after_is_unchanged()
    {
        var largest = TimeSpan.FromMilliseconds(MaxSupportedMs);

        Assert.Equal(largest, McpConnectTimeout.Normalize(largest));
    }

    [Fact]
    public void The_next_larger_whole_second_duration_is_normalized_to_infinite()
    {
        // Largest whole-second duration that still fits under the CancelAfter limit, and the
        // next whole second above it (which would make CancelAfter throw).
        var largestWholeSecond = TimeSpan.FromSeconds(MaxSupportedMs / 1000);
        var nextWholeSecond = largestWholeSecond + TimeSpan.FromSeconds(1);

        Assert.Equal(largestWholeSecond, McpConnectTimeout.Normalize(largestWholeSecond));
        Assert.Equal(Timeout.InfiniteTimeSpan, McpConnectTimeout.Normalize(nextWholeSecond));
    }

    [Fact]
    public void Explicit_timespan_overrides_are_normalized_like_environment_values()
    {
        // Normalize applies the same rules a manager would get from the environment, so an
        // explicit TimeSpan override (a later task) collapses to infinite exactly the same way.
        Assert.Equal(TimeSpan.FromSeconds(45), McpConnectTimeout.Normalize(TimeSpan.FromSeconds(45)));
        Assert.Equal(Timeout.InfiniteTimeSpan, McpConnectTimeout.Normalize(TimeSpan.Zero));
        Assert.Equal(Timeout.InfiniteTimeSpan, McpConnectTimeout.Normalize(TimeSpan.FromSeconds(-1)));
        Assert.Equal(Timeout.InfiniteTimeSpan, McpConnectTimeout.Normalize(Timeout.InfiniteTimeSpan));
        Assert.Equal(
            Timeout.InfiniteTimeSpan,
            McpConnectTimeout.Normalize(TimeSpan.FromMilliseconds((double)MaxSupportedMs + 1)));
    }
}
