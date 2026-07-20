using System.Text;

namespace Coda.Agent.Tasks;

/// <summary>
/// Bounded, thread-safe, drop-oldest text buffer. Tracks absolute character
/// offsets so incremental readers can detect when data they had not yet read
/// was evicted (Truncated == true).
///
/// Memory bounding is enforced two ways:
/// <list type="bullet">
/// <item>Tiny adjacent appends are coalesced into bounded-size chunks (see
/// <see cref="_chunkTarget"/>) so a stream of 1-byte appends produces a small,
/// bounded number of nodes rather than one node per append.</item>
/// <item>Whole chunks are evicted from the front once the total exceeds the cap.
/// A single append larger than the cap is trimmed to the newest UTF-8-valid
/// suffix that fits, so no unbounded payload is ever retained.</item>
/// </list>
///
/// Absolute UTF-16 cursor semantics are preserved: <see cref="TotalChars"/>
/// always counts every character ever appended (including trimmed/evicted ones),
/// and <see cref="DroppedChars"/> counts characters removed from the front. All
/// chunk boundaries (append, eviction, and oversized trimming) fall on Unicode
/// code-point boundaries, so buffered/returned strings are always valid UTF-16.
/// </summary>
internal sealed class OutputRing
{
    public const long DefaultMaxBytes = 1L << 20; // 1 MiB

    /// <summary>Upper bound on the payload size we coalesce into a single chunk.</summary>
    private const int MaxChunkTarget = 16 * 1024; // 16 KiB

    private sealed class Chunk
    {
        public Chunk(string text, int byteLength, long startChar)
        {
            Text = new StringBuilder(text);
            ByteLength = byteLength;
            StartChar = startChar;
        }

        public StringBuilder Text { get; }
        public int ByteLength { get; set; }
        public long StartChar { get; set; }
        public int CharLength => Text.Length;
    }

    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly int _chunkTarget;
    private readonly LinkedList<Chunk> _chunks = new();
    private long _byteLength;
    private long _totalChars;   // absolute chars ever appended
    private long _droppedChars; // absolute chars evicted from the front

    public OutputRing(long maxBytes = DefaultMaxBytes)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
        // Keep chunk granularity well below the cap so eviction stays reasonably
        // fine-grained, while capping tiny-append coalescing at 16 KiB payloads.
        _chunkTarget = (int)Math.Clamp(maxBytes / 16, 1, MaxChunkTarget);
    }

    /// <summary>Total characters ever appended, including evicted ones.</summary>
    public long TotalChars { get { lock (_gate) { return _totalChars; } } }

    /// <summary>Characters evicted from the front so far.</summary>
    public long DroppedChars { get { lock (_gate) { return _droppedChars; } } }

    /// <summary>Number of internal chunk nodes. Exposed for memory-bounding tests.</summary>
    internal int ChunkCount { get { lock (_gate) { return _chunks.Count; } } }

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var bytes = Encoding.UTF8.GetByteCount(text);
        lock (_gate)
        {
            var startChar = _totalChars;
            _totalChars += text.Length;

            var last = _chunks.Last?.Value;
            if (last is not null && last.ByteLength < _chunkTarget)
            {
                // Coalesce into the still-growing tail chunk (amortized O(1)).
                last.Text.Append(text);
                last.ByteLength += bytes;
            }
            else
            {
                _chunks.AddLast(new Chunk(text, bytes, startChar));
            }
            _byteLength += bytes;

            EvictWhileOverCap();
            TrimNewestIfOversized();
        }
    }

    private void EvictWhileOverCap()
    {
        // Keep at least the most recent chunk even if it alone exceeds the cap;
        // an oversized sole chunk is handled by TrimNewestIfOversized instead.
        while (_byteLength > _maxBytes && _chunks.Count > 1)
        {
            var first = _chunks.First!.Value;
            _chunks.RemoveFirst();
            _byteLength -= first.ByteLength;
            _droppedChars = first.StartChar + first.CharLength;
        }
    }

    /// <summary>
    /// When the only remaining chunk still exceeds the cap (an oversized single
    /// append), trim its prefix so only the newest UTF-8-valid suffix that fits
    /// <see cref="_maxBytes"/> is retained. TotalChars is unchanged (it counts
    /// the whole input); DroppedChars advances by the trimmed prefix length.
    /// </summary>
    private void TrimNewestIfOversized()
    {
        if (_chunks.Count != 1) return;
        var only = _chunks.First!.Value;
        if (only.ByteLength <= _maxBytes) return;

        var start = SuffixStartFitting(only.Text, _maxBytes);
        if (start <= 0) return;

        var retainedBytes = Utf8Bytes(only.Text, start, only.Text.Length - start);
        only.Text.Remove(0, start);
        _byteLength -= only.ByteLength - retainedBytes;
        only.ByteLength = retainedBytes;
        only.StartChar += start;
        _droppedChars = only.StartChar;
    }

    /// <summary>
    /// Returns the char index of the newest suffix of <paramref name="s"/> whose
    /// UTF-8 encoding fits in <paramref name="maxBytes"/>, cut on a code-point
    /// boundary (never splitting a surrogate pair). If even the last code point
    /// alone exceeds the cap, that whole rune is still retained (matching Peek's
    /// whole-rune rule) rather than returning an empty, lossy result.
    /// </summary>
    private static int SuffixStartFitting(StringBuilder s, long maxBytes)
    {
        long bytes = 0;
        var i = s.Length;
        while (i > 0)
        {
            var step = 1;
            if (char.IsLowSurrogate(s[i - 1]) && i >= 2 && char.IsHighSurrogate(s[i - 2]))
            {
                step = 2;
            }

            var cpBytes = Utf8Bytes(s, i - step, step);
            if (bytes + cpBytes > maxBytes)
            {
                if (i == s.Length) i -= step; // guarantee at least the newest rune
                break;
            }

            bytes += cpBytes;
            i -= step;
        }
        return i;
    }

    private static int Utf8Bytes(StringBuilder s, int start, int length) =>
        Encoding.UTF8.GetByteCount(s.ToString(start, length));

    /// <summary>
    /// Reads all buffered text at or after the absolute character offset
    /// <paramref name="cursor"/>. Returns the concatenated text, the next cursor
    /// to pass on the following call, and whether earlier data was evicted before
    /// the caller could read it (cursor &lt; DroppedChars). Negative cursors are
    /// clamped to zero.
    /// </summary>
    public (string Text, long NextCursor, bool Truncated) ReadFrom(long cursor)
    {
        lock (_gate)
        {
            return ReadFromNoLock(cursor);
        }
    }

    /// <summary>Returns the last <paramref name="maxChars"/> characters currently buffered.</summary>
    public string Peek(int maxChars)
    {
        if (maxChars <= 0) return string.Empty;
        lock (_gate)
        {
            var start = Math.Max(_droppedChars, _totalChars - maxChars);
            var (text, _, _) = ReadFromNoLock(start);

            // The requested boundary may land on the low surrogate of a pair.
            // Widen to include the preceding high surrogate so we return the whole
            // rune (2 code units even for maxChars == 1) instead of malformed
            // UTF-16. If the high half was already evicted, drop the lone low
            // surrogate rather than emit it.
            if (text.Length > 0 && char.IsLowSurrogate(text[0]))
            {
                if (start - 1 >= _droppedChars)
                {
                    (text, _, _) = ReadFromNoLock(start - 1);
                }
                else
                {
                    text = text[1..];
                }
            }

            return text;
        }
    }

    private (string Text, long NextCursor, bool Truncated) ReadFromNoLock(long cursor)
    {
        if (cursor < 0) cursor = 0;
        var truncated = cursor < _droppedChars;
        var from = Math.Max(cursor, _droppedChars);
        if (from >= _totalChars) return (string.Empty, _totalChars, truncated);

        var sb = new StringBuilder();
        foreach (var chunk in _chunks)
        {
            var chunkEnd = chunk.StartChar + chunk.CharLength;
            if (chunkEnd <= from) continue;
            var localStart = (int)Math.Max(0, from - chunk.StartChar);
            sb.Append(chunk.Text.ToString(localStart, chunk.CharLength - localStart));
        }
        return (sb.ToString(), _totalChars, truncated);
    }
}
