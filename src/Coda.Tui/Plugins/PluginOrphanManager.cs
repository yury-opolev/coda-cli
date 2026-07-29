namespace Coda.Tui.Plugins;

/// <summary>
/// Manages the 14-day orphan grace period for superseded plugin directories.
/// </summary>
/// <remarks>
/// <para>
/// When a plugin is updated its old directory is <em>moved</em> to orphan storage rather than
/// deleted immediately, because a concurrently-running Coda session may still hold open file
/// handles or path references to the old copy. Deleting it immediately would break that session.
/// </para>
/// <para>
/// On the next load, orphans that are older than 14 days are purged. This mirrors Claude Code's
/// rule for cached plugin versions.
/// </para>
/// <para>
/// Orphan directories are stored under
/// <c>&lt;codaDir&gt;/plugin-orphans/&lt;pluginName&gt;-&lt;ticks&gt;/</c>, where
/// <c>&lt;ticks&gt;</c> is the UTC timestamp (ticks) at the time of supersession. Injecting a
/// <see cref="TimeProvider"/> keeps tests deterministic without sleeping.
/// </para>
/// </remarks>
public static class PluginOrphanManager
{
    /// <summary>Duration a superseded plugin directory is retained before deletion.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(14);

    private const string OrphanSubdir = "plugin-orphans";

    /// <summary>
    /// Moves <paramref name="pluginDirectory"/> to orphan storage, stamped with the current UTC
    /// time from <paramref name="clock"/>. Does nothing if the directory does not exist.
    /// </summary>
    /// <param name="pluginDirectory">The plugin directory to supersede.</param>
    /// <param name="codaDir">The <c>.coda</c> base directory (orphans go under a subdirectory here).</param>
    /// <param name="clock">Clock to stamp the orphan entry — inject in tests.</param>
    public static void MoveToOrphan(string pluginDirectory, string codaDir, TimeProvider clock)
    {
        if (!Directory.Exists(pluginDirectory))
        {
            return;
        }

        var pluginName = Path.GetFileName(pluginDirectory);
        var timestamp = clock.GetUtcNow().UtcTicks;

        var orphanDir = Path.Combine(codaDir, OrphanSubdir);
        Directory.CreateDirectory(orphanDir);

        var dest = Path.Combine(orphanDir, $"{pluginName}-{timestamp}");
        Directory.Move(pluginDirectory, dest);
    }

    /// <summary>
    /// Deletes orphan directories whose timestamp is older than <see cref="GracePeriod"/>.
    /// Called on each plugin load to reclaim disk space without disrupting running sessions.
    /// </summary>
    /// <param name="codaDir">The <c>.coda</c> base directory.</param>
    /// <param name="clock">Clock used to evaluate age — inject in tests.</param>
    public static void PurgeExpired(string codaDir, TimeProvider clock)
    {
        var orphanDir = Path.Combine(codaDir, OrphanSubdir);
        if (!Directory.Exists(orphanDir))
        {
            return;
        }

        var now = clock.GetUtcNow();

        foreach (var dir in Directory.EnumerateDirectories(orphanDir))
        {
            var dirName = Path.GetFileName(dir);
            var dashIndex = dirName.LastIndexOf('-');
            if (dashIndex < 0)
            {
                continue;
            }

            var ticksPart = dirName[(dashIndex + 1)..];
            if (!long.TryParse(ticksPart, out var ticks))
            {
                continue;
            }

            DateTimeOffset orphanTime;
            try
            {
                orphanTime = new DateTimeOffset(ticks, TimeSpan.Zero);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            if (now - orphanTime > GracePeriod)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Best effort — another process might hold a handle. Skip and retry next load.
                }
            }
        }
    }

    /// <summary>
    /// Returns all currently stored orphan entries as
    /// <c>(directory, timestamp)</c> pairs, sorted oldest-first.
    /// </summary>
    internal static IReadOnlyList<(string Directory, DateTimeOffset Timestamp)> ListOrphans(string codaDir)
    {
        var orphanDir = Path.Combine(codaDir, OrphanSubdir);
        if (!Directory.Exists(orphanDir))
        {
            return [];
        }

        var result = new List<(string, DateTimeOffset)>();
        foreach (var dir in Directory.EnumerateDirectories(orphanDir))
        {
            var dirName = Path.GetFileName(dir);
            var dashIndex = dirName.LastIndexOf('-');
            if (dashIndex < 0)
            {
                continue;
            }

            if (!long.TryParse(dirName[(dashIndex + 1)..], out var ticks))
            {
                continue;
            }

            try
            {
                var ts = new DateTimeOffset(ticks, TimeSpan.Zero);
                result.Add((dir, ts));
            }
            catch (ArgumentOutOfRangeException)
            {
                // skip malformed entry
            }
        }

        result.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return result;
    }
}
