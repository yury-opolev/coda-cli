using System.Text;

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
        var task = Register(TaskKind.Shell, command, parentTaskId);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        void OnStdout(string chunk)
        {
            lock (stdout) { stdout.Append(chunk); }
            AppendOutput(task.Id, chunk);
        }

        void OnStderr(string chunk)
        {
            lock (stderr) { stderr.Append(chunk); }
            AppendOutput(task.Id, chunk);
        }

        string CapturedOut() { lock (stdout) { return stdout.ToString(); } }
        string CapturedErr() { lock (stderr) { return stderr.ToString(); } }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, task.Token);
        ManagedShellProcess? shell = null;
        try
        {
            shell = ManagedShellProcess.Start(command, workingDirectory);
            var (exitCode, timedOut) = await shell
                .RunToEndAsync(OnStdout, OnStderr, timeout, linked.Token)
                .ConfigureAwait(false);

            if (timedOut)
            {
                Fail(task.Id, "timed out");
                return new ShellRunResult(-1, CapturedOut(), CapturedErr(), TimedOut: true, Detached: false, task.Id);
            }

            if (exitCode == 0)
            {
                Complete(task.Id, $"exit code: {exitCode}");
            }
            else
            {
                Fail(task.Id, $"exit code: {exitCode}");
            }

            return new ShellRunResult(exitCode, CapturedOut(), CapturedErr(), TimedOut: false, Detached: false, task.Id);
        }
        catch (OperationCanceledException)
        {
            shell?.TryKillTree();
            Stop(task.Id);
            return new ShellRunResult(-1, CapturedOut(), CapturedErr(), TimedOut: false, Detached: false, task.Id);
        }
        catch (Exception ex)
        {
            shell?.TryKillTree();
            Fail(task.Id, ex.Message);
            return new ShellRunResult(-1, CapturedOut(), CapturedErr(), TimedOut: false, Detached: false, task.Id);
        }
        finally
        {
            shell?.Dispose();
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
        var task = Register(TaskKind.Shell, command, parentTaskId);

        _ = Task.Run(async () =>
        {
            ManagedShellProcess? shell = null;
            try
            {
                shell = ManagedShellProcess.Start(command, workingDirectory);
                var (exitCode, timedOut) = await shell
                    .RunToEndAsync(
                        chunk => AppendOutput(task.Id, chunk),
                        chunk => AppendOutput(task.Id, chunk),
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
