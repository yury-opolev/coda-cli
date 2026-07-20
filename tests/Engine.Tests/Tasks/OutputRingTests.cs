using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class OutputRingTests
{
    [Fact]
    public void ReadFrom_Zero_ReturnsAllAppendedText()
    {
        var ring = new OutputRing(maxBytes: 1024);
        ring.Append("hello ");
        ring.Append("world");
        var (text, next, truncated) = ring.ReadFrom(0);
        Assert.Equal("hello world", text);
        Assert.Equal(11, next);
        Assert.False(truncated);
    }

    [Fact]
    public void ReadFrom_Cursor_ReturnsOnlyNewText()
    {
        var ring = new OutputRing(maxBytes: 1024);
        ring.Append("abc");
        var first = ring.ReadFrom(0);
        ring.Append("def");
        var (text, next, truncated) = ring.ReadFrom(first.NextCursor);
        Assert.Equal("def", text);
        Assert.Equal(6, next);
        Assert.False(truncated);
    }

    [Fact]
    public void ReadFrom_UpToDate_ReturnsEmpty()
    {
        var ring = new OutputRing(maxBytes: 1024);
        ring.Append("abc");
        var (text, next, truncated) = ring.ReadFrom(3);
        Assert.Equal("", text);
        Assert.Equal(3, next);
        Assert.False(truncated);
    }

    [Fact]
    public void Append_BeyondCap_DropsOldestAndReportsTruncationToStaleCursor()
    {
        // 8-byte cap; append 12 ASCII bytes so the first chunk is evicted.
        var ring = new OutputRing(maxBytes: 8);
        ring.Append("aaaa");   // chars 0..3
        ring.Append("bbbb");   // chars 4..7
        ring.Append("cccc");   // chars 8..11 -> forces eviction of "aaaa"

        Assert.True(ring.DroppedChars >= 4);

        // A reader still at cursor 0 has missed evicted data.
        var (text, next, truncated) = ring.ReadFrom(0);
        Assert.True(truncated);
        Assert.EndsWith("cccc", text);
        Assert.Equal(12, next);
    }

    [Fact]
    public void ReadFrom_CursorAtOrAfterDropped_IsNotTruncated()
    {
        var ring = new OutputRing(maxBytes: 8);
        ring.Append("aaaa");
        ring.Append("bbbb");
        ring.Append("cccc"); // drops "aaaa"; DroppedChars == 4
        var (text, next, truncated) = ring.ReadFrom(ring.DroppedChars);
        Assert.False(truncated);
        Assert.Equal(12, next);
        Assert.Equal("bbbbcccc", text);
    }

    [Fact]
    public void Peek_ReturnsTailUpToMaxChars()
    {
        var ring = new OutputRing(maxBytes: 1024);
        ring.Append("0123456789");
        Assert.Equal("6789", ring.Peek(maxChars: 4));
    }

    [Fact]
    public void Peek_ShorterThanMax_ReturnsAll()
    {
        var ring = new OutputRing(maxBytes: 1024);
        ring.Append("hi");
        Assert.Equal("hi", ring.Peek(maxChars: 100));
    }

    [Fact]
    public void TotalChars_CountsAllAppendedIncludingDropped()
    {
        var ring = new OutputRing(maxBytes: 8);
        ring.Append("aaaa");
        ring.Append("bbbb");
        ring.Append("cccc");
        Assert.Equal(12, ring.TotalChars);
    }

    [Fact]
    public void DefaultMaxBytes_IsOneMebibyte()
    {
        Assert.Equal(1L << 20, OutputRing.DefaultMaxBytes);
    }

    [Fact]
    public void Append_MultibyteUtf8_EvictsByByteCountNotCharCount()
    {
        // "€" is 3 UTF-8 bytes but a single .NET char. With an 8-byte cap,
        // two 6-byte chunks (12 bytes) exceed the cap and evict the first,
        // proving eviction accounts for UTF-8 bytes rather than char length.
        var ring = new OutputRing(maxBytes: 8);
        ring.Append("\u20AC\u20AC"); // 6 bytes, chars 0..1
        ring.Append("\u20AC\u20AC"); // 6 bytes, chars 2..3 -> evicts first chunk

        Assert.Equal(2, ring.DroppedChars);
        Assert.Equal(4, ring.TotalChars);

        var (text, next, truncated) = ring.ReadFrom(0);
        Assert.True(truncated);
        Assert.Equal("\u20AC\u20AC", text);
        Assert.Equal(4, next);
    }

    [Fact]
    public void Append_OversizedNewestChunk_IsRetainedThenEvictedByNextChunk()
    {
        // A single chunk larger than the cap is retained (we never drop the
        // newest chunk), so it remains fully readable and nothing is dropped.
        var ring = new OutputRing(maxBytes: 4);
        ring.Append("abcdefgh"); // 8 bytes > 4-byte cap, but the only chunk

        Assert.Equal(0, ring.DroppedChars);
        var (text, next, truncated) = ring.ReadFrom(0);
        Assert.False(truncated);
        Assert.Equal("abcdefgh", text);
        Assert.Equal(8, next);

        // Once a newer chunk arrives, the oversized older chunk is evicted whole.
        ring.Append("ij");
        Assert.Equal(8, ring.DroppedChars);
        var after = ring.ReadFrom(0);
        Assert.True(after.Truncated);
        Assert.Equal("ij", after.Text);
        Assert.Equal(10, after.NextCursor);
    }
}
