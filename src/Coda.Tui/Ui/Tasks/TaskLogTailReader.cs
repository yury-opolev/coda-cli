using System.Text;

namespace Coda.Tui.Ui.Tasks;

/// <summary>The result of a tail read: decoded text plus a non-null diagnostic when the log was unreadable.</summary>
internal sealed record TaskLogTail(string Text, string? Error);

/// <summary>
/// Reads a bounded newest tail of a task's persistent log without ever blocking the writer or the UI.
/// The stream is opened read-only with a permissive, writer-compatible share mode
/// (<see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>) so it never blocks the
/// append-only writer (<see cref="FileShare.Read"/>) nor a size-cap rewrite (<see cref="FileMode.Create"/>),
/// and so a retention delete can proceed while a read is in flight. The read is capped to the newest
/// <c>maxBytes</c>, aligned to a UTF-8 code-point boundary at both ends (dangling continuation bytes at
/// the start and an incomplete multi-byte sequence at the end are dropped rather than decoded to
/// U+FFFD), and tolerant of a concurrent truncate/rewrite: on a short read the reader reseeks and
/// retries once from the fresh length, then returns whatever remains. A missing file or directory is an
/// empty (not error) result; only a genuine IO/permission failure sets <see cref="TaskLogTail.Error"/>.
/// Sanitization is intentionally NOT performed here — the caller (TaskBrowserController / the plain and
/// Spectre <c>/tasks</c> snapshots) runs <see cref="Rendering.TerminalTextSanitizer"/> so this reader
/// stays a pure, byte-faithful tail. The method never throws except to honor cancellation.
/// </summary>
internal static class TaskLogTailReader
{
    public const int DefaultMaxBytes = 64 * 1024;

    // Strict UTF-8: throw on invalid bytes instead of substituting U+FFFD. Boundary tears are trimmed
    // before decoding, so a throw here only signals genuinely corrupt interior bytes, handled below.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async Task<TaskLogTail> ReadTailAsync(
        string path, int maxBytes = DefaultMaxBytes, CancellationToken cancellationToken = default)
    {
        if (maxBytes <= 0)
        {
            return new TaskLogTail(string.Empty, null);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var options = new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                // Compatible with the writer's open append handle (Share=Read) AND a concurrent
                // FileMode.Create rewrite: allow other writers and deletion while we read.
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            };

            await using var stream = new FileStream(path, options);

            byte[] buffer;
            int read;
            long start;
            var attempt = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var length = stream.Length;
                start = length > maxBytes ? length - maxBytes : 0;

                // Seek to the newest tail from the current end-of-file view.
                stream.Seek(start, SeekOrigin.Begin);

                buffer = new byte[length - start];
                read = await ReadFullyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);

                // A short read means the file shrank mid-read (concurrent truncate/rewrite). Reseek and
                // retry once from the fresh length; a second short read just yields what remains.
                if (read == buffer.Length || attempt >= 1)
                {
                    break;
                }

                attempt++;
            }

            // When we started mid-file we may have landed inside a multi-byte code point; skip any
            // leading UTF-8 continuation bytes (0b10xxxxxx) so decoding starts on a clean boundary.
            var offset = start > 0 ? SkipLeadingContinuationBytes(buffer, read) : 0;

            // The newest bytes may also stop mid-code-point (writer captured mid-append); drop that
            // dangling lead+continuation run so we never decode a torn suffix to U+FFFD.
            var end = TrimIncompleteTrailingSequence(buffer, offset, read);

            var text = Decode(buffer, offset, end - offset);
            return new TaskLogTail(text, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return new TaskLogTail(string.Empty, null); // no log written yet is normal, not an error
        }
        catch (DirectoryNotFoundException)
        {
            return new TaskLogTail(string.Empty, null);
        }
        catch (IOException ex)
        {
            return new TaskLogTail(string.Empty, $"(log unavailable: {ex.Message})");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new TaskLogTail(string.Empty, $"(log unavailable: {ex.Message})");
        }
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break; // EOF (or the writer truncated the file mid-read): return what we have
            }

            total += n;
        }

        return total;
    }

    private static int SkipLeadingContinuationBytes(byte[] buffer, int length)
    {
        var i = 0;
        while (i < length && (buffer[i] & 0xC0) == 0x80)
        {
            i++;
        }

        return i;
    }

    private static int TrimIncompleteTrailingSequence(byte[] buffer, int start, int end)
    {
        if (end <= start)
        {
            return end;
        }

        // Walk back over trailing continuation bytes to the lead byte of the final code point (a UTF-8
        // code point is at most 4 bytes, so at most 3 trailing continuation bytes precede its lead).
        var lead = end - 1;
        var min = Math.Max(start, end - 4);
        while (lead >= min && (buffer[lead] & 0xC0) == 0x80)
        {
            lead--;
        }

        if (lead < start)
        {
            return end; // no lead byte in reach; let the decoder deal with it
        }

        var expected = SequenceLength(buffer[lead]);
        if (expected == 0)
        {
            return end; // not a valid lead byte; leave interior bytes to the strict/lenient decoder
        }

        var available = end - lead;
        return available < expected ? lead : end;
    }

    private static int SequenceLength(byte lead)
    {
        if ((lead & 0x80) == 0x00)
        {
            return 1;
        }

        if ((lead & 0xE0) == 0xC0)
        {
            return 2;
        }

        if ((lead & 0xF0) == 0xE0)
        {
            return 3;
        }

        if ((lead & 0xF8) == 0xF0)
        {
            return 4;
        }

        return 0;
    }

    private static string Decode(byte[] buffer, int index, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        try
        {
            return StrictUtf8.GetString(buffer, index, count);
        }
        catch (DecoderFallbackException)
        {
            // Genuinely malformed interior bytes are rare for a UTF-8 log; fall back to a lenient decode
            // so we still surface readable text rather than failing the whole tail read.
            return Encoding.UTF8.GetString(buffer, index, count);
        }
    }
}
