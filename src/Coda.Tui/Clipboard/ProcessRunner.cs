using System.Diagnostics;

namespace Coda.Tui.Clipboard;

/// <summary>The real <see cref="IProcessRunner"/>: launches the child process and captures stdout.</summary>
internal sealed class ProcessRunner : IProcessRunner
{
    public static readonly ProcessRunner Instance = new();

    public async Task<string?> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        Process? process = null;

        // The stderr pipe is drained concurrently with stdout: if the child wrote enough to a redirected
        // but unread stderr to fill the OS pipe buffer, it would block on the write while we block reading
        // stdout, deadlocking until the timeout. Kept as a field so the finally can observe (and thus not
        // leak as an unobserved task exception) a read that was cancelled by the timeout.
        Task<string>? stderrDrain = null;

        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            stderrDrain = process.StandardError.ReadToEndAsync(cts.Token);
            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (process is not null)
            {
                // The timeout must reap the child, not just abandon the wait: Process.Dispose() releases the
                // managed handles but never terminates a still-running process. Kill the whole tree so a hung
                // clipboard tool (and any piped child, e.g. base64 on Linux) cannot leak for the session.
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best-effort reap; cleanup never throws.
                }

                if (stderrDrain is not null)
                {
                    try
                    {
                        await stderrDrain.ConfigureAwait(false);
                    }
                    catch
                    {
                        // Observe a cancelled/failed stderr read so it isn't an unobserved task exception.
                    }
                }

                process.Dispose();
            }
        }
    }
}
