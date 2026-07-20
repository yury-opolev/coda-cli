using System.Text;
using Coda.Common;

namespace Coda.Agent.Tasks;

/// <summary>
/// Persistent, secret-redacted, UTF-8 (no BOM) append-only diagnostic log for a
/// single task. Owner-only where the OS supports it (file mode 0600 and containing
/// directory mode 0700 on Unix; Windows relies on the user-profile ACL).
///
/// Redaction is resilient to chunk boundaries: incoming text is accumulated in a
/// pending buffer and only complete lines are redacted and flushed, so a secret
/// split across multiple <see cref="Append"/> calls is joined before redaction and
/// never persisted. Any remaining pending text is redacted and flushed on
/// <see cref="Dispose"/>.
///
/// When appending would exceed <see cref="_maxBytes"/>, the newest UTF-8-valid
/// content that fits the cap is retained (never splitting a code point) rather than
/// resetting the log to empty or retaining unbounded content.
///
/// Logging is best-effort: any I/O or permission failure disables the writer
/// without throwing, so diagnostics never disrupt task execution.
/// </summary>
internal sealed class TaskLogWriter : IDisposable
{
    public const long DefaultMaxBytes = 50L * 1024 * 1024; // 50 MiB

    /// <summary>Force a flush of a runaway line without newlines beyond this size.</summary>
    private const int MaxPendingChars = 64 * 1024;

    /// <summary>
    /// Raw tail retained across a forced flush so a secret straddling the forced-flush
    /// boundary is re-joined and redacted on the next round. Chosen comfortably larger
    /// than any expected secret token.
    /// </summary>
    private const int OverlapChars = 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private readonly StringBuilder _pending = new();

    private StreamWriter? _writer;
    private long _bytesWritten;
    private bool _faulted;
    private bool _disposed;

    public TaskLogWriter(string path, long maxBytes = DefaultMaxBytes)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
    }

    /// <summary>Appends text (redacted on line boundaries). Never throws.</summary>
    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            if (_faulted || _disposed) return;
            try
            {
                _pending.Append(text);
                DrainCompleteLines();
                if (_pending.Length > MaxPendingChars)
                {
                    ForceDrain();
                }
            }
            catch
            {
                Fault();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_faulted && _pending.Length > 0)
                {
                    Emit(SecretRedactor.Redact(_pending.ToString()));
                    _pending.Clear();
                }
            }
            catch
            {
                // best-effort; swallow so Dispose never throws.
            }

            TryClose();
        }
    }

    /// <summary>Redacts and writes every complete line, leaving the trailing partial line buffered.</summary>
    private void DrainCompleteLines()
    {
        int lastNewline = LastIndexOf(_pending, '\n');
        if (lastNewline < 0) return;

        int flushLen = lastNewline + 1;
        var flushable = _pending.ToString(0, flushLen);
        _pending.Remove(0, flushLen);
        Emit(SecretRedactor.Redact(flushable));
    }

    /// <summary>
    /// Flushes a runaway newline-free buffer, retaining a raw overlap tail so a secret
    /// straddling the flush point is re-joined and redacted next round.
    /// </summary>
    private void ForceDrain()
    {
        int emitLen = _pending.Length - OverlapChars;
        if (emitLen <= 0) return;

        // Never cut in the middle of a surrogate pair.
        if (char.IsLowSurrogate(_pending[emitLen]))
        {
            emitLen--;
            if (emitLen <= 0) return;
        }

        var flushable = _pending.ToString(0, emitLen);
        _pending.Remove(0, emitLen);
        Emit(SecretRedactor.Redact(flushable));
    }

    /// <summary>Writes already-redacted text, enforcing the size cap by retaining the newest content.</summary>
    private void Emit(string redacted)
    {
        if (string.IsNullOrEmpty(redacted)) return;

        var bytes = Utf8NoBom.GetByteCount(redacted);
        EnsureOpen();

        if (_bytesWritten + bytes > _maxBytes)
        {
            WriteWithCap(redacted);
            return;
        }

        _writer!.Write(redacted);
        _writer.Flush();
        _bytesWritten += bytes;
    }

    /// <summary>
    /// The write would exceed the cap: combine the current file content with the new
    /// text, retain the newest UTF-8-valid suffix that fits the cap (never splitting a
    /// code point), and rewrite the file.
    /// </summary>
    private void WriteWithCap(string redacted)
    {
        _writer!.Flush();
        _writer.Dispose();
        _writer = null;

        string existing;
        try { existing = File.ReadAllText(_path, Utf8NoBom); }
        catch { existing = string.Empty; }

        var combined = existing + redacted;
        var kept = NewestSuffixWithinCap(combined, _maxBytes);

        var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(kept);
        writer.Flush();

        _writer = writer;
        _bytesWritten = Utf8NoBom.GetByteCount(kept);
        TrySetOwnerOnly();
    }

    private void EnsureOpen()
    {
        if (_writer is not null) return;

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            TrySetDirectoryOwnerOnly(dir);
        }

        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _bytesWritten = stream.Length;
        _writer = new StreamWriter(stream, Utf8NoBom);
        TrySetOwnerOnly();
    }

    /// <summary>
    /// Returns the newest suffix of <paramref name="s"/> whose UTF-8 encoding fits in
    /// <paramref name="maxBytes"/>, cut on a code-point boundary. If even the last rune
    /// alone exceeds the cap, that whole rune is still retained rather than emitting an
    /// empty, lossy result.
    /// </summary>
    private static string NewestSuffixWithinCap(string s, long maxBytes)
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

            var runeBytes = Utf8NoBom.GetByteCount(s.Substring(i - step, step));
            if (bytes + runeBytes > maxBytes)
            {
                if (i == s.Length) i -= step; // guarantee at least the newest rune
                break;
            }

            bytes += runeBytes;
            i -= step;
        }

        return i <= 0 ? s : s.Substring(i);
    }

    private static int LastIndexOf(StringBuilder sb, char c)
    {
        for (var i = sb.Length - 1; i >= 0; i--)
        {
            if (sb[i] == c) return i;
        }

        return -1;
    }

    private void TrySetOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // best-effort; a filesystem that rejects chmod must not fault the writer.
        }
    }

    private static void TrySetDirectoryOwnerOnly(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            // best-effort.
        }
    }

    private void Fault()
    {
        _faulted = true;
        TryClose();
    }

    private void TryClose()
    {
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
    }
}
