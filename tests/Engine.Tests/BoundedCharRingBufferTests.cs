using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// <see cref="BoundedCharRingBuffer"/> is a fixed-size character ring buffer: O(1) appends,
/// oldest-character eviction once full, a <see cref="BoundedCharRingBuffer.Count"/> capped at
/// capacity, and ordered materialization of the retained tail. It underpins bounded MCP stderr
/// diagnostics so a hostile child cannot force unbounded memory growth.
/// </summary>
public sealed class BoundedCharRingBufferTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1024)]
    public void Constructor_rejects_non_positive_capacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedCharRingBuffer(capacity));
    }

    [Fact]
    public void Below_capacity_preserves_order_and_count()
    {
        var buffer = new BoundedCharRingBuffer(5);

        foreach (var c in "abc")
        {
            buffer.Append(c);
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal(5, buffer.Capacity);
        Assert.Equal("abc", buffer.ToOrderedString());
    }

    [Fact]
    public void Overflow_retains_exact_last_capacity_characters()
    {
        var buffer = new BoundedCharRingBuffer(3);

        foreach (var c in "abcde")
        {
            buffer.Append(c);
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal("cde", buffer.ToOrderedString());
    }

    [Fact]
    public void Multiple_wraparound_cycles_retain_latest_tail()
    {
        var buffer = new BoundedCharRingBuffer(4);

        // Append 26 characters through the 4-slot buffer several times over.
        foreach (var c in "abcdefghijklmnopqrstuvwxyz")
        {
            buffer.Append(c);
        }

        Assert.Equal(4, buffer.Count);
        Assert.Equal("wxyz", buffer.ToOrderedString());
    }

    [Fact]
    public void Count_never_exceeds_capacity()
    {
        var buffer = new BoundedCharRingBuffer(2);

        for (var i = 0; i < 1000; i++)
        {
            buffer.Append((char)('a' + (i % 26)));
            Assert.True(buffer.Count <= buffer.Capacity);
        }

        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void Clear_resets_and_allows_reuse()
    {
        var buffer = new BoundedCharRingBuffer(3);
        foreach (var c in "abcde")
        {
            buffer.Append(c);
        }

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(string.Empty, buffer.ToOrderedString());

        foreach (var c in "xy")
        {
            buffer.Append(c);
        }

        Assert.Equal(2, buffer.Count);
        Assert.Equal("xy", buffer.ToOrderedString());
    }
}
