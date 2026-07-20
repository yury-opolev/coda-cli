using System.Diagnostics;
using Coda.Common;
using LlmClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Agent;

public sealed partial class ProcessShellExecutor : IShellExecutor
{
    private readonly ILogger logger;
    private readonly string toolName;

    /// <param name="logger">Optional telemetry logger; when set, start/completion of the command are logged at Debug.</param>
    /// <param name="toolName">Name of the calling tool, included in telemetry (defaults to "shell").</param>
    public ProcessShellExecutor(ILogger? logger = null, string toolName = "shell")
    {
        this.logger = logger ?? NullLogger.Instance;
        this.toolName = toolName;
    }

    public async Task<ShellResult> RunAsync(string command, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        this.LogToolStart(this.toolName, command, workingDirectory);
        var stopwatch = Stopwatch.StartNew();
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

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Close the child's stdin immediately so it gets EOF and never inherits
        // or uses our stdin pipe. In serve mode our stdin is an open JSON-RPC
        // pipe that never reaches EOF; a child inheriting it would block forever
        // (the serve-mode run_command deadlock).
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        // Read both pipes concurrently (avoids a full-buffer deadlock). The
        // reads observe the timeout token (linked to the caller's token) so a
        // lingering grandchild holding a pipe handle cannot stall them past the
        // timeout — they are cancelled alongside the exit wait.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            // Await exit AND both reads. A lingering grandchild that inherits the
            // stdout/stderr pipe handles can keep them open after the shell exits,
            // so the reads alone could hang long past the timeout if they were not
            // governed by it; the reads observe timeoutCts.Token (see above), and
            // WaitForExitAsync does too, so Task.WhenAll already honors the timeout.
            await Task.WhenAll(process.WaitForExitAsync(timeoutCts.Token), stdoutTask, stderrTask)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ANY abnormal exit from the wait/read region — timeout, user
            // cancellation, or an unexpected fault (e.g. an IOException from a
            // broken pipe) — must kill the whole process tree before propagating,
            // so we never leak a running process or leave a held pipe handle. The
            // kill is in a finally-equivalent position: it runs before every branch
            // below regardless of which exception was thrown.
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }

            // Observe the read tasks so their cancellation/faults are not left
            // unobserved once we stop awaiting them. Do not block on them: a held
            // pipe handle could keep a read pending even after the kill, and the
            // whole point here is to return/propagate promptly.
            Observe(stdoutTask);
            Observe(stderrTask);

            // Genuine user cancellation surfaces as OperationCanceledException.
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            // Timed out — the timeout token fired while the caller's did not.
            // Return a simple result with no output (caller shows a timeout message).
            if (ex is OperationCanceledException && timeoutCts.IsCancellationRequested)
            {
                stopwatch.Stop();
                this.LogToolDone(this.toolName, -1, stopwatch.ElapsedMilliseconds, true, string.Empty, string.Empty);
                return new ShellResult(-1, "", "", TimedOut: true);
            }

            // Any other abnormal fault (e.g. broken pipe): the tree is already
            // killed; propagate so the caller sees the real failure.
            throw;
        }

        // Reads have already completed above; these awaits just unwrap results.
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        stopwatch.Stop();
        this.LogToolDone(
            this.toolName,
            process.ExitCode,
            stopwatch.ElapsedMilliseconds,
            false,
            TelemetryText.Truncate(stdout),
            TelemetryText.Truncate(stderr));
        return new ShellResult(process.ExitCode, stdout, stderr, TimedOut: false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "tool start: {tool}, command='{command}', cwd={cwd}")]
    private partial void LogToolStart(string tool, string command, string cwd);

    [LoggerMessage(Level = LogLevel.Debug, Message = "tool done: {tool}, exit={exitCode}, {durationMs}ms, timedOut={timedOut}, stdout='{stdoutPreview}', stderr='{stderrPreview}'")]
    private partial void LogToolDone(string tool, int exitCode, long durationMs, bool timedOut, string stdoutPreview, string stderrPreview);

    // Marks a task observed so a cancellation/fault on the abort path does not
    // surface later as an unobserved task exception.
    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
