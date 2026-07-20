namespace Coda.Agent.Tasks;

/// <summary>
/// Startup housekeeping for the persistent task-log tree. Recursively deletes logs
/// older than <see cref="MaxAge"/> and then, newest-first, deletes older logs once
/// the total size exceeds <see cref="GlobalCapBytes"/>. Best-effort: missing or
/// unreadable roots and individual delete failures are ignored and never throw.
/// </summary>
internal static class TaskLogRetention
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public const long GlobalCapBytes = 512L * 1024 * 1024; // 512 MiB

    public static void Cleanup(string root, TimeSpan maxAge, long globalCapBytes) =>
        Cleanup(root, maxAge, globalCapBytes, TimeProvider.System);

    /// <summary>
    /// Overload accepting a <see cref="TimeProvider"/> seam so age-based deletion can
    /// be tested deterministically without depending on the wall clock.
    /// </summary>
    public static void Cleanup(string root, TimeSpan maxAge, long globalCapBytes, TimeProvider timeProvider)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(root)
                .GetFiles("*.log", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }
        catch
        {
            return; // enumeration failed (e.g. permissions); nothing safe to do.
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // 1) Age-based deletion.
        var survivors = new List<FileInfo>();
        foreach (var f in files)
        {
            if (now - f.LastWriteTimeUtc > maxAge)
            {
                TryDelete(f);
            }
            else
            {
                survivors.Add(f);
            }
        }

        // 2) Global-cap deletion, newest-first: keep newest until the cap is hit.
        long total = 0;
        foreach (var f in survivors) // already newest-first
        {
            total += f.Length;
            if (total > globalCapBytes)
            {
                TryDelete(f);
            }
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try { file.Delete(); } catch { /* best-effort */ }
    }
}
