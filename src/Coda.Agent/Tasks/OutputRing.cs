using System.Text;

namespace Coda.Agent.Tasks;

/// <summary>
/// Bounded, thread-safe, drop-oldest text buffer. Tracks absolute character
/// offsets so incremental readers can detect when data they had not yet read
/// was evicted (Truncated == true). Eviction happens whole-chunk.
/// </summary>
public sealed class OutputRing
{
    public const long DefaultMaxBytes = 1L << 20; // 1 MiB

    private sealed record Chunk(string Text, int ByteLength, long StartChar);

    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly LinkedList<Chunk> _chunks = new();
    private long _byteLength;
    private long _totalChars;   // absolute chars ever appended
    private long _droppedChars; // absolute chars evicted from the front

    public OutputRing(long maxBytes = DefaultMaxBytes)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
    }

    /// <summary>Total characters ever appended, including evicted ones.</summary>
    public long TotalChars { get { lock (_gate) { return _totalChars; } } }

    /// <summary>Characters evicted from the front so far.</summary>
    public long DroppedChars { get { lock (_gate) { return _droppedChars; } } }

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var bytes = Encoding.UTF8.GetByteCount(text);
        lock (_gate)
        {
            _chunks.AddLast(new Chunk(text, bytes, _totalChars));
            _byteLength += bytes;
            _totalChars += text.Length;
            EvictWhileOverCap();
        }
    }

    private void EvictWhileOverCap()
    {
        // Keep at least the most recent chunk even if it alone exceeds the cap.
        while (_byteLength > _maxBytes && _chunks.Count > 1)
        {
            var first = _chunks.First!.Value;
            _chunks.RemoveFirst();
            _byteLength -= first.ByteLength;
            _droppedChars = first.StartChar + first.Text.Length;
        }
    }

    /// <summary>
    /// Reads all buffered text at or after the absolute character offset
    /// <paramref name="cursor"/>. Returns the concatenated text, the next cursor
    /// to pass on the following call, and whether earlier data was evicted before
    /// the caller could read it (cursor &lt; DroppedChars).
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
            return text;
        }
    }

    private (string Text, long NextCursor, bool Truncated) ReadFromNoLock(long cursor)
    {
        var truncated = cursor < _droppedChars;
        var from = Math.Max(cursor, _droppedChars);
        if (from >= _totalChars) return (string.Empty, _totalChars, truncated);

        var sb = new StringBuilder();
        foreach (var chunk in _chunks)
        {
            var chunkEnd = chunk.StartChar + chunk.Text.Length;
            if (chunkEnd <= from) continue;
            var localStart = (int)Math.Max(0, from - chunk.StartChar);
            sb.Append(chunk.Text, localStart, chunk.Text.Length - localStart);
        }
        return (sb.ToString(), _totalChars, truncated);
    }
}
