using System;
using System.IO;

namespace Coda.Common;

/// <summary>
/// Crash-safe file writes.
/// </summary>
/// <remarks>
/// <para>
/// Writing JSON state in place risks leaving a truncated file if the process dies mid-write, so
/// every writer in the codebase writes to a sibling temporary file and renames it over the target.
/// The rename is atomic, so a reader sees either the old contents or the new ones, never a partial
/// document.
/// </para>
/// <para>
/// The rename can still fail — a virus scanner holding the target open, a full disk, a revoked
/// permission — and when it does the temporary file must be removed. Without that cleanup every
/// failed write leaves a <c>.tmp</c> file behind, and they accumulate indefinitely because nothing
/// else ever deletes them.
/// </para>
/// </remarks>
public static class AtomicFile
{
    /// <summary>
    /// Temporary files older than this are considered abandoned by a dead process. A real write
    /// completes in milliseconds, so nothing legitimate is ever this old.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically, creating the
    /// containing directory if needed and removing the temporary file if the write fails.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var dir = string.IsNullOrWhiteSpace(directory) ? "." : directory;
        var name = Path.GetFileName(path);
        var temp = Path.Combine(dir, TempName(name));

        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
            temp = null;
        }
        finally
        {
            if (temp is not null)
            {
                TryDelete(temp);
            }
        }

        SweepStale(dir, name);
    }

    /// <summary>The temporary file name used for a given target file name.</summary>
    public static string TempName(string fileName) => $".{fileName}.{Guid.NewGuid():N}.tmp";

    /// <summary>
    /// Asynchronous counterpart of <see cref="WriteAllText(string, string)"/>.
    /// </summary>
    public static async System.Threading.Tasks.Task WriteAllTextAsync(
        string path,
        string contents,
        System.Threading.CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var dir = string.IsNullOrWhiteSpace(directory) ? "." : directory;
        var name = Path.GetFileName(path);
        var temp = Path.Combine(dir, TempName(name));

        try
        {
            await File.WriteAllTextAsync(temp, contents, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            temp = null;
        }
        finally
        {
            if (temp is not null)
            {
                TryDelete(temp);
            }
        }

        SweepStale(dir, name);
    }

    /// <summary>
    /// Removes abandoned temporary files left by earlier crashed or failed writes.
    /// </summary>
    /// <remarks>
    /// Only files matching this target's temporary pattern and older than <see cref="StaleAfter"/>
    /// are removed, so a write running concurrently in another process is never disturbed.
    /// Historic writers used a truncated stem (<c>.settings.*.tmp</c> for <c>settings.json</c>), so
    /// that form is swept too.
    /// </remarks>
    private static void SweepStale(string directory, string fileName)
    {
        var cutoff = DateTime.UtcNow - StaleAfter;

        foreach (var pattern in TempPatterns(fileName))
        {
            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(directory, pattern);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                return;
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(candidate) < cutoff)
                    {
                        File.Delete(candidate);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another process may hold it; leaving it is harmless.
                }
            }
        }
    }

    /// <summary>Temporary-file globs belonging to <paramref name="fileName"/>.</summary>
    private static string[] TempPatterns(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return string.Equals(stem, fileName, StringComparison.Ordinal)
            ? [$".{fileName}.*.tmp"]
            : [$".{fileName}.*.tmp", $".{stem}.*.tmp"];
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: the write already failed, and a leaked temp file is
            // never loaded by anything.
        }
    }
}
