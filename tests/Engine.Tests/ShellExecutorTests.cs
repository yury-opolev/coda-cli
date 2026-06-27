using Coda.Agent;

namespace Engine.Tests;

public sealed class ShellExecutorTests
{
    private readonly string workingDirectory;

    public ShellExecutorTests()
    {
        this.workingDirectory = Path.Combine(Path.GetTempPath(), "coda-shell-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.workingDirectory);
    }

    [Fact]
    public async Task Echo_returns_stdout_exit_zero()
    {
        var executor = new ProcessShellExecutor();

        var result = await executor.RunAsync("echo hello", this.workingDirectory, TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Stdout);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Failing_command_nonzero_exit()
    {
        var executor = new ProcessShellExecutor();
        var command = OperatingSystem.IsWindows() ? "exit 3" : "exit 3";

        var result = await executor.RunAsync(command, this.workingDirectory, TimeSpan.FromSeconds(30));

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Timeout_sets_TimedOut()
    {
        var executor = new ProcessShellExecutor();
        var command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 10" : "sleep 10";

        var result = await executor.RunAsync(command, this.workingDirectory, TimeSpan.FromSeconds(1));

        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task Child_reading_stdin_sees_eof_and_returns_promptly()
    {
        var executor = new ProcessShellExecutor();
        // The child reads everything on its stdin to EOF. In serve mode coda's
        // own stdin is an OPEN JSON-RPC pipe that never reaches EOF, so a child
        // that inherits it blocks forever (the serve-mode run_command deadlock).
        // The fix redirects stdin and closes it immediately after Start(), so
        // the child always sees EOF and reports a zero-length read regardless of
        // what coda's real stdin is.
        //
        // NOTE: this asserts the guaranteed post-fix contract rather than acting
        // as a standalone RED reproduction. We cannot non-invasively reproduce
        // the inherited-open-pipe block from inside the test host: under the
        // test runner the process's stdin is already at EOF, so the inherited
        // path also returns LEN=0. Forcing an open inherited stdin would require
        // mutating the runner's global STD_INPUT_HANDLE, which risks corrupting
        // the host. The deadlock is instead exercised end-to-end by serve mode;
        // this test is the forward regression guard. The companion
        // Lingering_child_holding_output_pipe_does_not_hang_past_timeout test is
        // a true RED reproduction of the related unguarded-pipe-read defect.
        var command = OperatingSystem.IsWindows()
            ? "$i=[Console]::In.ReadToEnd(); Write-Output ('LEN=' + $i.Length)"
            : "i=$(cat); echo \"LEN=${#i}\"";

        var result = await executor
            .RunAsync(command, this.workingDirectory, TimeSpan.FromSeconds(15))
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("LEN=0", result.Stdout);
    }

    [Fact]
    public async Task User_cancellation_kills_tree_and_throws_promptly_even_with_lingering_pipe()
    {
        var executor = new ProcessShellExecutor();
        // Same lingering-grandchild setup as the timeout test, but here the CALLER's
        // token is cancelled mid-run rather than the timeout firing. The broadened
        // abnormal-exit handler must kill the whole process tree (releasing the held
        // pipe handles) and rethrow OperationCanceledException promptly, instead of
        // blocking forever on a ReadToEndAsync that never sees EOF.
        var command = OperatingSystem.IsWindows()
            ? "Start-Process powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 30' -NoNewWindow; Start-Sleep -Seconds 30"
            : "(sleep 30 0<&- 1>&0 2>&0 &) >/dev/null 2>&1; sleep 30";

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // A very long timeout so ONLY the caller's cancellation can end the run.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor
            .RunAsync(command, this.workingDirectory, TimeSpan.FromSeconds(120), cts.Token))
            .WaitAsync(TimeSpan.FromSeconds(20));
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"RunAsync should throw shortly after cancellation, took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Lingering_child_holding_output_pipe_does_not_hang_past_timeout()
    {
        var executor = new ProcessShellExecutor();
        // Spawn a detached grandchild that outlives the shell and inherits (and
        // keeps open) the shell's stdout/stderr pipe handles. The shell itself
        // exits within a second, so WaitForExitAsync succeeds — but a naive
        // ReadToEndAsync would never observe EOF on the pipes (the grandchild
        // still holds the write ends) and would hang for the full 30s. The
        // reads must therefore be governed by the same timeout as the exit
        // wait, so RunAsync returns shortly after the 3s timeout instead.
        var command = OperatingSystem.IsWindows()
            ? "Start-Process powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 30' -NoNewWindow; exit 0"
            : "(sleep 30 0<&- 1>&0 2>&0 &) >/dev/null 2>&1; sleep 30 &";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await executor
            .RunAsync(command, this.workingDirectory, TimeSpan.FromSeconds(3))
            .WaitAsync(TimeSpan.FromSeconds(20));
        stopwatch.Stop();

        Assert.True(result.TimedOut);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"RunAsync should return shortly after the 3s timeout, took {stopwatch.Elapsed}.");
    }
}
