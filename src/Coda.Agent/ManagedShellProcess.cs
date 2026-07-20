using System.Diagnostics;

namespace Coda.Agent;

/// <summary>
/// Runs a shell command as a managed process: closes stdin (no writable stdin), streams stdout
/// and stderr incrementally to per-stream sinks as bytes arrive, and tree-kills the whole process
/// group on stop, timeout, or fault. Backs both foreground and background shell tasks.
/// </summary>
public sealed class ManagedShellProcess : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    private ManagedShellProcess(Process process) => _process = process;

    /// <summary>The OS process id of the managed shell (for tree-ownership tracking).</summary>
    public int ProcessId => _process.Id;

    /// <summary>Starts the command in <paramref name="workingDirectory"/> with stdin closed.</summary>
    public static ManagedShellProcess Start(string command, string workingDirectory)
    {
        var (shell, args) = ShellCommandLine.For(command);
        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch
        {
            // The process never started, so there is no tree to kill; dispose the handle
            // so a failed start never leaks a Process object and rethrow for the caller
            // (RunShellAsync/StartShellBackground) to mark the task Failed.
            process.Dispose();
            throw;
        }

        // No writable stdin: close immediately so the child sees EOF and never blocks
        // inheriting our (possibly never-EOF) stdin pipe — the serve-mode deadlock guard.
        process.StandardInput.Close();
        return new ManagedShellProcess(process);
    }

    /// <summary>
    /// Pumps stdout into <paramref name="onStdout"/> and stderr into <paramref name="onStderr"/>
    /// until the process exits, then returns its exit code. Honors <paramref name="timeout"/>
    /// (<see cref="Timeout.InfiniteTimeSpan"/> disables it) and <paramref name="cancellationToken"/>;
    /// on either the whole tree is killed and TimedOut reflects a timeout (not user cancellation).
    /// </summary>
    public async Task<(int ExitCode, bool TimedOut)> RunToEndAsync(
        Action<string> onStdout,
        Action<string> onStderr,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(timeout);
        }

        var stdoutTask = PumpAsync(_process.StandardOutput, onStdout, timeoutCts.Token);
        var stderrTask = PumpAsync(_process.StandardError, onStderr, timeoutCts.Token);

        try
        {
            await Task.WhenAll(_process.WaitForExitAsync(timeoutCts.Token), stdoutTask, stderrTask)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryKillTree();
            Observe(stdoutTask);
            Observe(stderrTask);

            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (ex is OperationCanceledException && timeoutCts.IsCancellationRequested)
            {
                return (-1, true);
            }

            throw;
        }

        return (_process.ExitCode, false);
    }

    /// <summary>Kills the whole process tree (idempotent, best-effort).</summary>
    public void TryKillTree()
    {
        try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onOutput, CancellationToken ct)
    {
        var buffer = new char[4096];
        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (read == 0)
            {
                return;
            }

            onOutput(new string(buffer, 0, read));
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryKillTree();
        _process.Dispose();
    }
}
