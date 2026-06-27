using System.Globalization;
using System.Text;

namespace Coda.Sdk.Telemetry;

/// <summary>
/// Appends JSON lines to a per-session log file under a directory, rolling to a new
/// numbered part when the current file exceeds a byte cap, bounding the number of
/// parts per run with a ring buffer, and pruning old runs. Thread-safe; best-effort
/// on all filesystem operations.
/// </summary>
public sealed class JsonLinesFileWriter : IDisposable
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly long maxFileSizeBytes;
    private readonly int maxRunParts;
    private readonly string sessionStem;
    private readonly List<string> runParts = [];
    private StreamWriter? stream;
    private string currentPath = string.Empty;
    private long currentBytes;
    private int nextPartNumber;
    private bool disposed;

    public JsonLinesFileWriter(string directory, long maxFileSizeBytes, int maxRunParts, string? sessionStem = null)
    {
        this.directory = directory;
        this.maxFileSizeBytes = maxFileSizeBytes;
        this.maxRunParts = maxRunParts;
        this.sessionStem = sessionStem ?? BuildSessionStem();
        Directory.CreateDirectory(this.directory);
        this.OpenNextPart();
    }

    /// <summary>The file currently being written.</summary>
    public string CurrentFilePath
    {
        get
        {
            lock (this.gate)
            {
                return this.currentPath;
            }
        }
    }

    /// <summary>
    /// The default per-session file stem: <c>coda-yyyyMMdd-HHmmss-&lt;pid&gt;-&lt;token&gt;</c>.
    /// The trailing random token disambiguates two sessions created in the SAME process
    /// within the same second (e.g. serve building a fresh session right after another):
    /// without it both would resolve to an identical filename and the second
    /// <see cref="FileMode.Create"/> would throw <see cref="IOException"/> against the
    /// first session's still-open <see cref="FileShare.Read"/> handle. The token is part of
    /// the stem, so per-run grouping (<see cref="StemOf"/>/<see cref="PruneRuns"/>) is
    /// unaffected.
    /// </summary>
    public static string BuildSessionStem()
    {
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var token = Guid.NewGuid().ToString("N")[..6];
        return $"coda-{ts}-{Environment.ProcessId}-{token}";
    }

    /// <summary>Appends one already-serialized JSON line (a newline is added).</summary>
    public void WriteLine(string jsonLine)
    {
        lock (this.gate)
        {
            if (this.disposed || this.stream is null)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetByteCount(jsonLine) + 1;
            if (this.maxFileSizeBytes > 0 && this.currentBytes > 0 && this.currentBytes + bytes > this.maxFileSizeBytes)
            {
                this.OpenNextPart();
            }

            this.stream.WriteLine(jsonLine);
            this.stream.Flush();
            this.currentBytes += bytes;
        }
    }

    /// <summary>
    /// Deletes whole runs (grouped by stem) beyond the newest <paramref name="retainedRuns"/>.
    /// Best-effort: locked/in-use files are skipped. No-op when retainedRuns &lt;= 0.
    /// </summary>
    public static void PruneRuns(string directory, int retainedRuns)
    {
        if (retainedRuns <= 0 || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            // Newest run first. Stems sort lexicographically; the leading
            // yyyyMMdd-HHmmss timestamp dominates, so order is correct except for two
            // runs started in the same second, where the trailing pid orders by string
            // (not numeric) value — an acceptable tie-break for retention purposes.
            var byStem = Directory.GetFiles(directory, "coda-*.log")
                .GroupBy(StemOf)
                .OrderByDescending(g => g.Key, StringComparer.Ordinal)
                .ToList();

            foreach (var group in byStem.Skip(retainedRuns))
            {
                foreach (var file in group)
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Best-effort: never throw out of cleanup.
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.stream?.Flush();
            this.stream?.Dispose();
            this.stream = null;
        }
    }

    private void OpenNextPart()
    {
        this.stream?.Flush();
        this.stream?.Dispose();

        var fileName = this.nextPartNumber == 0
            ? $"{this.sessionStem}.log"
            : $"{this.sessionStem}.{this.nextPartNumber}.log";
        this.currentPath = Path.Combine(this.directory, fileName);
        this.nextPartNumber++;

        this.stream = new StreamWriter(new FileStream(this.currentPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = false,
        };
        this.currentBytes = 0;

        this.runParts.Add(this.currentPath);
        this.EnforceRingBuffer();
    }

    private void EnforceRingBuffer()
    {
        if (this.maxRunParts <= 0)
        {
            return;
        }

        while (this.runParts.Count > this.maxRunParts)
        {
            var oldest = this.runParts[0];
            this.runParts.RemoveAt(0);
            TryDelete(oldest);
        }
    }

    /// <summary>
    /// The run-grouping key: the file name minus the optional <c>.N</c> part suffix
    /// and the <c>.log</c> extension. e.g. <c>coda-...-3.1.log</c> → <c>coda-...-3</c>.
    /// </summary>
    private static string StemOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path); // strips ".log"
        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0 && int.TryParse(name.AsSpan(lastDot + 1), out _))
        {
            return name[..lastDot];
        }

        return name;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked/in-use: skip.
        }
    }
}
