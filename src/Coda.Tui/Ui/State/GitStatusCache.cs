using System.Diagnostics;

namespace Coda.Tui.Ui.State;

/// <summary>
/// A turn-scoped cache in front of the git working-tree probe, keyed by working directory. Each
/// directory is probed at most once per turn: the result is reused until <see cref="InvalidateAfterTurn"/>
/// is called. Concurrent callers for the same directory coalesce onto one in-flight probe, and a
/// caller cancelling its own <see cref="GetAsync"/> does not cancel the shared probe. The caller's
/// cancellation is never swallowed.
/// </summary>
public sealed class GitStatusCache
{
    /// <summary>How long the default probe waits for <c>git</c> before giving up.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<string, CancellationToken, Task<GitStatus>> probe;
    private readonly object gate = new();
    private readonly Dictionary<string, GitStatus> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<GitStatus>> inFlight = new(StringComparer.Ordinal);

    public GitStatusCache(Func<string, CancellationToken, Task<GitStatus>> probe)
    {
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>
    /// The production cache: probes <c>git status --porcelain=v1 --branch</c> (2s timeout) and parses
    /// branch/dirty, returning <c>GitStatus(null, false)</c> when git is absent or the directory is
    /// not a repository.
    /// </summary>
    public static GitStatusCache CreateDefault() => new(ProbeGitAsync);

    /// <summary>Drop all cached statuses so the next <see cref="GetAsync"/> re-probes.</summary>
    public void InvalidateAfterTurn()
    {
        lock (this.gate)
        {
            this.cache.Clear();
        }
    }

    /// <summary>
    /// Returns the git status for <paramref name="workingDirectory"/>, probing lazily and reusing the
    /// result until invalidated. Concurrent callers for the same directory share one probe. The
    /// caller's <paramref name="cancellationToken"/> cancels only this call's wait, never the shared
    /// probe, and is propagated (not swallowed).
    /// </summary>
    public Task<GitStatus> GetAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        Task<GitStatus> shared;
        lock (this.gate)
        {
            if (this.cache.TryGetValue(workingDirectory, out var cached))
            {
                return Task.FromResult(cached);
            }

            if (!this.inFlight.TryGetValue(workingDirectory, out shared!))
            {
                shared = this.StartProbe(workingDirectory);
            }
        }

        return AwaitSharedAsync(shared, cancellationToken);
    }

    private static async Task<GitStatus> AwaitSharedAsync(Task<GitStatus> shared, CancellationToken cancellationToken)
    {
        return await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the (uncancellable) shared probe for a directory, records it as that directory's single
    /// in-flight probe, and arranges reference-tracked cleanup. Must be called while holding <see cref="gate"/>.
    /// </summary>
    private Task<GitStatus> StartProbe(string workingDirectory)
    {
        // Uncancellable shared probe: one caller's cancellation must not abort a probe others share.
        var task = this.probe(workingDirectory, CancellationToken.None);
        this.inFlight[workingDirectory] = task;
        _ = this.ObserveAsync(workingDirectory, task);
        return task;
    }

    private async Task ObserveAsync(string workingDirectory, Task<GitStatus> task)
    {
        try
        {
            var status = await task.ConfigureAwait(false);
            lock (this.gate)
            {
                this.cache[workingDirectory] = status;
                this.RemoveInFlight(workingDirectory, task);
            }
        }
        catch
        {
            lock (this.gate)
            {
                this.RemoveInFlight(workingDirectory, task);
            }
        }
    }

    private void RemoveInFlight(string workingDirectory, Task<GitStatus> task)
    {
        if (this.inFlight.TryGetValue(workingDirectory, out var current) && ReferenceEquals(current, task))
        {
            this.inFlight.Remove(workingDirectory);
        }
    }

    private static async Task<GitStatus> ProbeGitAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // ArgumentList (never a shell string) — no argument is interpolated, so no shell injection.
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--porcelain=v1");
            psi.ArgumentList.Add("--branch");

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return new GitStatus(null, false);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProbeTimeout);

            var readTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The 2s timeout fired (not the caller). Kill the process and report "unknown".
                TryKill(process);
                return new GitStatus(null, false);
            }

            if (process.ExitCode != 0)
            {
                return new GitStatus(null, false);
            }

            var output = await readTask.ConfigureAwait(false);
            return ParsePorcelain(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Never swallow the caller's cancellation.
            throw;
        }
        catch
        {
            // git missing, not a repository, or any other failure → status unknown.
            return new GitStatus(null, false);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort — the process may already have exited.
        }
    }

    /// <summary>
    /// Parses <c>git status --porcelain=v1 --branch</c> output: the branch from the leading
    /// <c>## &lt;branch&gt;...&lt;upstream&gt;</c> header, dirty when any file-status lines follow.
    /// </summary>
    private static GitStatus ParsePorcelain(string output)
    {
        string? branch = null;
        var dirty = false;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var rest = line[3..];
                var upstream = rest.IndexOf("...", StringComparison.Ordinal);
                var name = upstream >= 0 ? rest[..upstream] : rest;

                // "## HEAD (no branch)" and similar detached headers carry a space.
                var space = name.IndexOf(' ');
                if (space >= 0)
                {
                    name = name[..space];
                }

                branch = name.Length > 0 ? name : null;
            }
            else
            {
                dirty = true;
            }
        }

        return new GitStatus(branch, dirty);
    }
}
