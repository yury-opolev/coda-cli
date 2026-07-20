using System.Text;
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class OutputRingTests
{
    [Fact]
    public void OutputRing_IsNotPublic()
    {
        var type = typeof(OutputRing);
        Assert.False(type.IsPublic, "OutputRing must not be public; it is an internal buffering type.");
        Assert.True(type.IsNotPublic);
    }

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
    public void Append_OversizedAscii_RetainsOnlyNewestSuffixFittingCap()
    {
        // A single append larger than the cap is trimmed to the newest suffix
        // that fits maxBytes; the dropped prefix is reflected in DroppedChars
        // while TotalChars still counts the entire input.
        var ring = new OutputRing(maxBytes: 4);
        ring.Append("abcdefgh"); // 8 bytes > 4-byte cap -> retain "efgh"

        Assert.Equal(8, ring.TotalChars);
        Assert.Equal(4, ring.DroppedChars);

        var (text, next, truncated) = ring.ReadFrom(0);
        Assert.True(truncated);
        Assert.Equal("efgh", text);
        Assert.Equal(8, next);
    }

    [Fact]
    public void Append_OversizedMultibyteBmp_TrimsOnCodepointBoundary()
    {
        // "€" is 3 UTF-8 bytes, 1 char. Four of them = 12 bytes. With an 8-byte
        // cap the newest suffix fitting is two "€" (6 bytes); three would be 9.
        var ring = new OutputRing(maxBytes: 8);
        ring.Append("\u20AC\u20AC\u20AC\u20AC"); // 12 bytes, 4 chars

        Assert.Equal(4, ring.TotalChars);
        Assert.Equal(2, ring.DroppedChars);

        var (text, _, truncated) = ring.ReadFrom(0);
        Assert.True(truncated);
        Assert.Equal("\u20AC\u20AC", text);
        Assert.Equal(6, Encoding.UTF8.GetByteCount(text));
    }

    [Fact]
    public void Append_OversizedEmoji_TrimsWithoutSplittingSurrogatePair()
    {
        // "😀" (U+1F600) is 4 UTF-8 bytes and a surrogate pair (2 chars).
        // Three emoji = 12 bytes / 6 chars. With an 8-byte cap the newest suffix
        // fitting is two whole emoji (8 bytes); the string stays valid UTF-16.
        const string emoji = "\uD83D\uDE00";
        var ring = new OutputRing(maxBytes: 8);
        ring.Append(emoji + emoji + emoji);

        Assert.Equal(6, ring.TotalChars);
        Assert.Equal(2, ring.DroppedChars);

        var (text, _, truncated) = ring.ReadFrom(0);
        Assert.True(truncated);
        Assert.Equal(emoji + emoji, text);
        Assert.Equal(8, Encoding.UTF8.GetByteCount(text));
        // Valid UTF-16 round-trips through UTF-8 with no replacement chars.
        Assert.Equal(text, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Peek_TailSplitsSurrogatePair_ReturnsWholeRuneNotLoneSurrogate()
    {
        // maxChars=1 lands on the low surrogate of the emoji; Peek must widen to
        // include the high surrogate and return the whole rune (2 code units).
        const string emoji = "\uD83D\uDE00";
        var ring = new OutputRing(maxBytes: 1024);
        ring.Append(emoji);

        var peeked = ring.Peek(maxChars: 1);
        Assert.Equal(emoji, peeked);
        Assert.Equal(2, peeked.Length); // whole rune, not a single lone surrogate
    }

    [Fact]
    public void ManyTinyAppends_ProduceBoundedChunkCount()
    {
        // 1-byte appends must be coalesced into bounded-size chunks so the node
        // count stays small instead of growing one node per append.
        var ring = new OutputRing(maxBytes: 64 * 1024);
        for (var i = 0; i < 200_000; i++)
        {
            ring.Append("a");
        }

        Assert.Equal(200_000, ring.TotalChars);
        Assert.True(ring.DroppedChars > 0, "expected eviction once cap exceeded");
        Assert.True(ring.ChunkCount < 64, $"expected bounded chunk count, got {ring.ChunkCount}");
    }

    [Fact]
    public void ReadFrom_NegativeCursor_ClampedToZeroNotTruncated()
    {
        var ring = new OutputRing(maxBytes: 1024);
        ring.Append("abc");
        var (text, next, truncated) = ring.ReadFrom(-5);
        Assert.False(truncated);
        Assert.Equal("abc", text);
        Assert.Equal(3, next);
    }

    // Blocking waits with timeouts keep this concurrency smoke test bounded and
    // deterministic; async/await would not exercise the lock under contention.
#pragma warning disable xUnit1031
    [Fact]
    public void ConcurrentAppendAndRead_IsThreadSafe()
    {
        var ring = new OutputRing(maxBytes: 64 * 1024);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(() =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested && n < 100_000)
            {
                ring.Append("x");
                n++;
            }
        });

        var reader = Task.Run(() =>
        {
            long cursor = 0;
            while (!writer.IsCompleted)
            {
                var (_, next, _) = ring.ReadFrom(cursor);
                cursor = next;
                _ = ring.Peek(16);
            }
        });

        Assert.True(Task.WaitAll(new[] { writer, reader }, TimeSpan.FromSeconds(10)));
        Assert.Equal(100_000, ring.TotalChars);
    }
#pragma warning restore xUnit1031
}
