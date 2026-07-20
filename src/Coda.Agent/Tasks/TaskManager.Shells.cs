namespace Coda.Agent.Tasks;

public sealed partial class TaskManager
{
    /// <summary>
    /// Runs a shell command as a foreground task: registers a shell task (so it is observable and
    /// stoppable for its whole lifetime), streams its stdout/stderr into the task ring/log as they
    /// arrive, and returns the captured output plus exit code. Both streams are also captured
    /// separately so callers (e.g. <c>run_command</c>) keep exact stdout/stderr.
    /// </summary>
    public async Task<ShellRunResult> RunShellAsync(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        string? parentTaskId = null,
        CancellationToken cancellationToken = default)
    {
        var task = Register(TaskKind.Shell, command, parentTaskId, TaskExecutionMode.Foreground);
        var capture = new ShellOutputCapture();

        // stdout/stderr are captured separately (so the caller keeps exact per-stream output) and
        // streamed into the ring/log on their own channels (so interleaving never corrupts or
        // leaks a secret straddling chunk boundaries on either stream).
        void OnStdout(string chunk)
        {
            capture.AppendStdout(chunk);
            AppendOutput(task.Id, chunk, TaskOutputChannel.Stdout);
        }

        void OnStderr(string chunk)
        {
            capture.AppendStderr(chunk);
            AppendOutput(task.Id, chunk, TaskOutputChannel.Stderr);
        }

        ManagedShellProcess shell;
        try
        {
            shell = ManagedShellProcess.Start(command, workingDirectory);
        }
        catch (Exception ex)
        {
            Fail(task.Id, ex.Message);
            return new ShellRunResult(-1, string.Empty, string.Empty, TimedOut: false, Detached: false, task.Id);
        }

        // Give the task direct ownership of the live process so graceful shutdown can tree-kill it
        // explicitly (not only via token cancellation). The handle is cleared when the process is
        // disposed, so no dead reference is retained.
        task.AttachShellKill(shell.TryKillTree);
        shell.OnDisposed = task.DetachShellKill;

        // The process lifetime is governed ONLY by the task's own token, never by the originating
        // turn's cancellationToken — that is what lets a *detached* shell outlive the turn that
        // started it. Turn cancellation is observed separately below and matters only while the
        // shell is still in the foreground (before a successful detach).
        var runTask = shell.RunToEndAsync(OnStdout, OnStderr, timeout, task.Token);

        var turnCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var turnRegistration = cancellationToken.Register(
            static s => ((TaskCompletionSource)s!).TrySetResult(), turnCancelled);

        // Race completion against (a) a promotion request and (b) turn cancellation.
        var completed = await Task.WhenAny(runTask, task.DetachRequested, turnCancelled.Task).ConfigureAwait(false);

        // (a) Detach wins: hand the still-running process to a background finalizer bound only to
        // task.Token and return immediately. Do NOT dispose/kill here — the shell keeps streaming,
        // and disposing the turn registration means turn cancellation can no longer reach it.
        // Atomically disable+snapshot capture first so the finalizer's continued pumping streams
        // only into the ring/log and cannot grow the capture buffers unbounded.
        if (completed == task.DetachRequested)
        {
            var (stdoutSnap, stderrSnap) = capture.DisableAndSnapshot();
            _ = FinalizeDetachedShellAsync(task, runTask, shell);
            return new ShellRunResult(-1, stdoutSnap, stderrSnap, TimedOut: false, Detached: true, task.Id);
        }

        // (b) Turn cancelled before any detach: the foreground still honors it — kill the tree,
        // drain the now-terminating run, mark the task stopped, and return.
        if (completed != runTask)
        {
            using (shell)
            {
                shell.TryKillTree();
                try { await runTask.ConfigureAwait(false); } catch { /* killed process may fault */ }
                Stop(task.Id);
                return new ShellRunResult(-1, capture.Stdout, capture.Stderr, TimedOut: false, Detached: false, task.Id);
            }
        }

        using (shell)
        {
            try
            {
                var (exitCode, timedOut) = await runTask.ConfigureAwait(false);
                if (timedOut)
                {
                    Fail(task.Id, "timed out");
                    return new ShellRunResult(-1, capture.Stdout, capture.Stderr, TimedOut: true, Detached: false, task.Id);
                }

                if (exitCode == 0)
                {
                    Complete(task.Id, $"exit code: {exitCode}");
                }
                else
                {
                    Fail(task.Id, $"exit code: {exitCode}");
                }

                return new ShellRunResult(exitCode, capture.Stdout, capture.Stderr, TimedOut: false, Detached: false, task.Id);
            }
            catch (OperationCanceledException)
            {
                shell.TryKillTree();
                Stop(task.Id);
                return new ShellRunResult(-1, capture.Stdout, capture.Stderr, TimedOut: false, Detached: false, task.Id);
            }
            catch (Exception ex)
            {
                shell.TryKillTree();
                Fail(task.Id, ex.Message);
                return new ShellRunResult(-1, capture.Stdout, capture.Stderr, TimedOut: false, Detached: false, task.Id);
            }
        }
    }

    /// <summary>
    /// Finalizes a shell task that was promoted to the background: awaits the still-running process
    /// (bound only to the task's own token, so the originating turn's cancellation can no longer kill
    /// it), sets its terminal status, and owns disposal of the process.
    /// </summary>
    private async Task FinalizeDetachedShellAsync(
        ManagedTask task,
        Task<(int ExitCode, bool TimedOut)> runTask,
        ManagedShellProcess shell)
    {
        using (shell)
        {
            try
            {
                var (exitCode, timedOut) = await runTask.ConfigureAwait(false);
                if (timedOut)
                {
                    Fail(task.Id, "timed out");
                }
                else if (exitCode == 0)
                {
                    Complete(task.Id, $"exit code: {exitCode}");
                }
                else
                {
                    Fail(task.Id, $"exit code: {exitCode}");
                }
            }
            catch (OperationCanceledException)
            {
                shell.TryKillTree();
                Stop(task.Id);
            }
            catch (Exception ex)
            {
                shell.TryKillTree();
                Fail(task.Id, ex.Message);
            }
        }
    }

    /// <summary>
    /// Starts a shell command as a background task and returns its id immediately; its output is
    /// polled via <c>task_output</c> and it is cancelled via <c>task_stop</c>.
    /// </summary>
    public string StartShellBackground(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        string? parentTaskId = null)
    {
        var task = Register(TaskKind.Shell, command, parentTaskId, TaskExecutionMode.Background);

        _ = Task.Run(async () =>
        {
            ManagedShellProcess? shell = null;
            try
            {
                shell = ManagedShellProcess.Start(command, workingDirectory);
                // Give the task direct ownership of the live process so graceful shutdown can
                // tree-kill it explicitly. Cleared when the process is disposed (below).
                task.AttachShellKill(shell.TryKillTree);
                shell.OnDisposed = task.DetachShellKill;
                var (exitCode, timedOut) = await shell
                    .RunToEndAsync(
                        chunk => AppendOutput(task.Id, chunk, TaskOutputChannel.Stdout),
                        chunk => AppendOutput(task.Id, chunk, TaskOutputChannel.Stderr),
                        timeout,
                        task.Token)
                    .ConfigureAwait(false);

                if (timedOut)
                {
                    Fail(task.Id, "timed out");
                }
                else if (exitCode == 0)
                {
                    Complete(task.Id, $"exit code: {exitCode}");
                }
                else
                {
                    Fail(task.Id, $"exit code: {exitCode}");
                }
            }
            catch (OperationCanceledException)
            {
                shell?.TryKillTree();
                Stop(task.Id);
            }
            catch (Exception ex)
            {
                shell?.TryKillTree();
                Fail(task.Id, ex.Message);
            }
            finally
            {
                shell?.Dispose();
            }
        });

        return task.Id;
    }
}
