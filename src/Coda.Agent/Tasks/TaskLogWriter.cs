using System.Text;

namespace Coda.Agent.Tasks;

/// <summary>
/// Persistent, secret-redacted, UTF-8 (no BOM) append-only diagnostic log for a
/// single task. Owner-only where the OS supports it: on Unix the file is created
/// with mode 0600 and its containing directory with 0700 <em>atomically</em> (via
/// the create-time Unix mode APIs, so there is no chmod-only exposure window);
/// Windows relies on the user-profile ACL.
///
/// Redaction is resilient to chunk boundaries: incoming text is streamed through a
/// per-channel <see cref="StreamingSecretRedactor"/> that confirms a secret as soon as its
/// minimum length is reached, emits a placeholder, and discards the remaining token
/// characters. A secret split across many <see cref="Append(string, TaskOutputChannel)"/> calls
/// on the same channel — even one far larger than any buffer — is therefore never persisted,
/// and interleaved writes on a different channel cannot corrupt or leak it because each channel
/// keeps its own redactor state.
///
/// When appending would exceed <see cref="_maxBytes"/>, the log is trimmed to a
/// code-point-valid newest suffix that leaves headroom, so sustained writing past
/// the cap costs only an amortized, bounded number of rewrites rather than one per
/// append.
///
/// Logging is best-effort: any I/O or permission failure disables the writer
/// without throwing, so diagnostics never disrupt task execution.
/// </summary>
internal sealed class TaskLogWriter : IDisposable
{
    public const long DefaultMaxBytes = 50L * 1024 * 1024; // 50 MiB

    /// <summary>Smallest headroom to reclaim on a trim, so tiny caps still amortize.</summary>
    private const long MinHeadroomBytes = 512;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    // One independent streaming-redactor state per output channel. Keeping the states separate
    // means a secret split across chunks on one channel is never spliced together with — and
    // therefore never corrupted or leaked by — interleaved chunks on another channel. Indexed by
    // (int)TaskOutputChannel; the scratch buffer is shared because every Append holds _gate.
    private readonly StreamingSecretRedactor[] _redactors =
    {
        new(), // General
        new(), // Stdout
        new(), // Stderr
    };
    private readonly StringBuilder _scratch = new();

    private StreamWriter? _writer;
    private long _bytesWritten;
    private int _trimCount;
    private bool _faulted;
    private bool _disposed;

    public TaskLogWriter(string path, long maxBytes = DefaultMaxBytes)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
    }

    /// <summary>
    /// Number of cap-enforcing trims performed. Instrumentation seam for tests that
    /// assert sustained post-cap writing amortizes to a small, bounded trim count.
    /// </summary>
    internal int TrimCount => _trimCount;

    /// <summary>
    /// Test seam for the trim-time read of existing log content. When null, the real
    /// file is read. A read failure faults the writer without overwriting the prior log.
    /// </summary>
    internal Func<string, string>? ReadExistingOverride { get; set; }

    /// <summary>
    /// Appends text on the given channel (streamed through that channel's independent redactor).
    /// Never throws. Each channel keeps its own redactor state so interleaved writes from
    /// different channels cannot corrupt or leak a secret straddling chunk boundaries.
    /// </summary>
    public void Append(string text, TaskOutputChannel channel = TaskOutputChannel.General)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            if (_faulted || _disposed) return;
            try
            {
                _scratch.Clear();
                _redactors[(int)channel].Process(text, _scratch);
                if (_scratch.Length > 0)
                {
                    Emit(_scratch.ToString());
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
                if (!_faulted)
                {
                    // Flush every channel's trailing unconfirmed candidate so nothing buffered is
                    // silently dropped.
                    foreach (var redactor in _redactors)
                    {
                        _scratch.Clear();
                        redactor.Flush(_scratch);
                        if (_scratch.Length > 0)
                        {
                            Emit(_scratch.ToString());
                        }
                    }
                }
            }
            catch
            {
                // best-effort; swallow so Dispose never throws.
            }

            TryClose();
        }
    }

    /// <summary>Writes already-redacted text, enforcing the size cap by amortized trimming.</summary>
    private void Emit(string redacted)
    {
        if (string.IsNullOrEmpty(redacted)) return;

        EnsureOpen();
        if (_writer is null) return; // open faulted

        var bytes = Utf8NoBom.GetByteCount(redacted);
        if (_bytesWritten + bytes <= _maxBytes)
        {
            _writer.Write(redacted);
            _writer.Flush();
            _bytesWritten += bytes;
            return;
        }

        TrimAndWrite(redacted, bytes);
    }

    /// <summary>
    /// Appending <paramref name="redacted"/> would exceed the cap. Trim the existing log to a
    /// newest suffix that leaves headroom, then rewrite it followed by the new text. Because a
    /// trim reclaims a fixed fraction of the cap, a run of small appends past the cap triggers
    /// only an amortized, bounded number of trims instead of a rewrite each time.
    /// </summary>
    private void TrimAndWrite(string redacted, int newBytes)
    {
        // Close the append handle before rewriting the file.
        _writer!.Flush();
        _writer.Dispose();
        _writer = null;

        var headroom = _maxBytes / 4;
        if (headroom < MinHeadroomBytes) headroom = MinHeadroomBytes;
        var maxHeadroom = Math.Max(1, _maxBytes / 2);
        if (headroom > maxHeadroom) headroom = maxHeadroom;
        var keptNew = redacted;
        string tail;

        if (newBytes > _maxBytes)
        {
            // Even the incoming text alone exceeds the cap: keep only its newest valid suffix.
            keptNew = NewestSuffixWithinCap(redacted, _maxBytes);
            tail = string.Empty;
        }
        else
        {
            var keepExistingBudget = _maxBytes - headroom - newBytes;
            if (keepExistingBudget <= 0)
            {
                tail = string.Empty;
            }
            else
            {
                string existing;
                try
                {
                    existing = ReadExisting();
                }
                catch
                {
                    // Reading the prior log failed: fault without overwriting it.
                    Fault();
                    return;
                }

                tail = NewestSuffixWithinCap(existing, keepExistingBudget);
            }
        }

        var combined = tail.Length == 0 ? keptNew : string.Concat(tail, keptNew);

        try
        {
            var stream = OpenStream(FileMode.Create);
            var writer = new StreamWriter(stream, Utf8NoBom);
            writer.Write(combined);
            writer.Flush();
            _writer = writer;
            _bytesWritten = Utf8NoBom.GetByteCount(combined);
        }
        catch
        {
            Fault();
            return;
        }

        _trimCount++;
    }

    private string ReadExisting() =>
        ReadExistingOverride is { } read ? read(_path) : File.ReadAllText(_path, Utf8NoBom);

    private void EnsureOpen()
    {
        if (_writer is not null) return;

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            CreateDirectoryRestrictive(dir);
        }

        var stream = OpenStream(FileMode.Append);
        _bytesWritten = stream.Length;
        _writer = new StreamWriter(stream, Utf8NoBom);
    }

    /// <summary>
    /// Opens the log file, creating it (on Unix) with mode 0600 atomically at creation time
    /// so there is no window during which the file is world-readable.
    /// </summary>
    private FileStream OpenStream(FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.Read,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(_path, options);
    }

    /// <summary>
    /// Creates the containing directory, on Unix with mode 0700 atomically at creation time.
    /// </summary>
    private static void CreateDirectoryRestrictive(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
        }
        else
        {
            Directory.CreateDirectory(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Returns the newest suffix of <paramref name="s"/> whose UTF-8 encoding fits in
    /// <paramref name="maxBytes"/>, cut on a code-point boundary. If even the last rune alone
    /// exceeds the cap, that whole rune is still retained rather than emitting an empty result.
    /// Byte sizing is computed arithmetically per rune, with a single final substring.
    /// </summary>
    private static string NewestSuffixWithinCap(string s, long maxBytes)
    {
        if (maxBytes <= 0 || s.Length == 0) return string.Empty;

        long bytes = 0;
        var i = s.Length;
        while (i > 0)
        {
            int step;
            int codePoint;
            var last = s[i - 1];
            if (char.IsLowSurrogate(last) && i >= 2 && char.IsHighSurrogate(s[i - 2]))
            {
                step = 2;
                codePoint = char.ConvertToUtf32(s[i - 2], last);
            }
            else
            {
                step = 1;
                codePoint = last;
            }

            var runeBytes = Utf8ByteLength(codePoint);
            if (bytes + runeBytes > maxBytes)
            {
                if (i == s.Length) i -= step; // guarantee at least the newest rune
                break;
            }

            bytes += runeBytes;
            i -= step;
        }

        return i <= 0 ? s : s[i..];
    }

    private static int Utf8ByteLength(int codePoint) => codePoint switch
    {
        < 0x80 => 1,
        < 0x800 => 2,
        < 0x10000 => 3,
        _ => 4,
    };

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
