using System.Text;

namespace Coda.Client.Framing;

/// <summary>
/// Incremental decoder that lifts whole frames out of a byte stream that
/// arrives in arbitrary chunks.
///
/// Two stream realities drive the whole design: a single frame can be split
/// across many reads, and several frames can arrive coalesced in one read. The
/// decoder therefore keeps its own buffer across <see cref="Feed"/> calls and
/// yields frames one at a time from <see cref="TryReadFrame"/>; a fresh decoder
/// per read — the classic bug — would silently drop the second of two coalesced
/// frames.
///
/// It is not thread-safe: a single reader loop owns it. Fatal desynchronisation
/// surfaces as <see cref="FramingException"/>, which the transport turns into a
/// visible connection failure rather than trying to resynchronise on a stream
/// whose frame boundaries are no longer known.
/// </summary>
public sealed class FrameDecoder
{
    /// <summary>Largest header block tolerated before assuming the peer is not
    /// speaking this framing at all.</summary>
    public const int MaxHeaderBytes = 8 * 1024;

    /// <summary>Upper bound on a single message body. Assistant text streams in
    /// deltas, so a legitimate frame is far below this; the cap exists so a
    /// corrupt or hostile length cannot ask us to buffer gigabytes.</summary>
    public const int MaxBodyBytes = 64 * 1024 * 1024;

    private byte[] buffer = new byte[4096];
    private int start;
    private int end;

    // Set once a header block has been consumed and we are waiting for its
    // body. Keeping it separate from the header scan is what lets a body split
    // across reads resume without re-parsing the header.
    private int? pendingBodyLength;

    /// <summary>Appends freshly read bytes to the internal buffer.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(this.buffer.AsSpan(this.end));
        this.end += bytes.Length;
    }

    /// <summary>
    /// Pulls one complete frame out of the buffer, returning <c>false</c> when
    /// more bytes are still needed. The returned payload is the raw body bytes,
    /// exactly <c>Content-Length</c> long, headers already stripped.
    /// </summary>
    public bool TryReadFrame(out byte[] frame)
    {
        while (true)
        {
            if (this.pendingBodyLength is int bodyLength)
            {
                if (Buffered < bodyLength)
                {
                    frame = Array.Empty<byte>();
                    return false;
                }

                frame = this.buffer.AsSpan(this.start, bodyLength).ToArray();
                this.start += bodyLength;
                this.pendingBodyLength = null;
                CompactIfNeeded();
                return true;
            }

            if (!TryConsumeHeaders(out int contentLength))
            {
                frame = Array.Empty<byte>();
                return false;
            }

            this.pendingBodyLength = contentLength;
        }
    }

    private int Buffered => this.end - this.start;

    /// <summary>
    /// Consumes a header block if one is fully buffered, yielding its
    /// <c>Content-Length</c>. Returns <c>false</c> when the terminator has not
    /// arrived yet.
    /// </summary>
    private bool TryConsumeHeaders(out int contentLength)
    {
        contentLength = 0;

        int terminatorLength = FindHeaderTerminator(out int terminatorOffset);
        if (terminatorLength == 0)
        {
            // No blank line yet. A header block that has already outgrown the
            // cap without terminating is never going to, so fail now instead
            // of buffering without limit.
            if (Buffered > MaxHeaderBytes)
            {
                throw new FramingException(
                    FramingErrorKind.HeaderTooLarge,
                    $"header block exceeded {MaxHeaderBytes} bytes without a terminator");
            }

            return false;
        }

        int headerByteCount = terminatorOffset - this.start;
        ReadOnlySpan<byte> headerBytes = this.buffer.AsSpan(this.start, headerByteCount);

        string headers;
        try
        {
            headers = StrictUtf8.GetString(headerBytes);
        }
        catch (DecoderFallbackException)
        {
            throw new FramingException(
                FramingErrorKind.HeaderNotUtf8,
                "header line is not valid UTF-8");
        }

        int? parsed = null;
        foreach (string rawLine in headers.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                throw new FramingException(
                    FramingErrorKind.MalformedHeader,
                    $"malformed header line: \"{line}\"");
            }

            string name = line[..colon].Trim();
            if (name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            {
                string value = line[(colon + 1)..].Trim();
                if (!int.TryParse(value, out int length) || length < 0)
                {
                    throw new FramingException(
                        FramingErrorKind.InvalidContentLength,
                        $"invalid Content-Length value: \"{value}\"");
                }

                parsed = length;
            }

            // Content-Type and any other header are accepted and ignored.
        }

        if (parsed is not int len)
        {
            throw new FramingException(
                FramingErrorKind.MissingContentLength,
                "missing Content-Length header");
        }

        if (len > MaxBodyBytes)
        {
            throw new FramingException(
                FramingErrorKind.BodyTooLarge,
                $"message body of {len} bytes exceeds the {MaxBodyBytes} byte limit");
        }

        this.start = terminatorOffset + terminatorLength;
        contentLength = len;
        return true;
    }

    /// <summary>
    /// Locates the blank line ending the header block, accepting both
    /// <c>\r\n\r\n</c> and bare <c>\n\n</c> terminators. Returns the terminator
    /// length (0 when not found) and, via <paramref name="terminatorOffset"/>,
    /// the absolute index where the terminator begins.
    /// </summary>
    private int FindHeaderTerminator(out int terminatorOffset)
    {
        ReadOnlySpan<byte> span = this.buffer.AsSpan(this.start, Buffered);
        for (int i = 0; i < span.Length; i++)
        {
            if (i + 4 <= span.Length &&
                span[i] == (byte)'\r' && span[i + 1] == (byte)'\n' &&
                span[i + 2] == (byte)'\r' && span[i + 3] == (byte)'\n')
            {
                terminatorOffset = this.start + i;
                return 4;
            }

            if (i + 2 <= span.Length && span[i] == (byte)'\n' && span[i + 1] == (byte)'\n')
            {
                terminatorOffset = this.start + i;
                return 2;
            }
        }

        terminatorOffset = 0;
        return 0;
    }

    private void EnsureCapacity(int extra)
    {
        if (this.end + extra <= this.buffer.Length)
        {
            return;
        }

        // Reclaiming the already-consumed prefix often makes room without a
        // larger allocation, which matters for a long-lived stream of small
        // frames where the read cursor keeps advancing.
        if (this.start > 0)
        {
            Compact();
        }

        if (this.end + extra <= this.buffer.Length)
        {
            return;
        }

        int required = this.end + extra;
        int capacity = this.buffer.Length;
        while (capacity < required)
        {
            capacity *= 2;
        }

        Array.Resize(ref this.buffer, capacity);
    }

    private void CompactIfNeeded()
    {
        // Keep the live window near the front so a steady stream does not walk
        // the cursor off the end of an ever-growing buffer.
        if (this.start >= 4096 && this.start * 2 >= this.end)
        {
            Compact();
        }
    }

    private void Compact()
    {
        int length = Buffered;
        if (length > 0 && this.start > 0)
        {
            Array.Copy(this.buffer, this.start, this.buffer, 0, length);
        }

        this.start = 0;
        this.end = length;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
