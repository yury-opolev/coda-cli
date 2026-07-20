using System.Diagnostics;
using Coda.Agent;
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class ShellTaskTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-sh", logRoot: null);

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);

    private static string SleepCommand(int seconds) =>
        OperatingSystem.IsWindows() ? $"Start-Sleep -Seconds {seconds}" : $"sleep {seconds}";

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "coda-shelltask-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ShellCommandLine_For_SelectsPlatformShell()
    {
        var (file, args) = ShellCommandLine.For("echo hi");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("powershell.exe", file);
            Assert.Contains("-NonInteractive", args);
            Assert.Equal("echo hi", args[^1]);
        }
        else
        {
            Assert.Equal("/bin/bash", file);
            Assert.Equal("-c", args[0]);
            Assert.Equal("echo hi", args[1]);
        }
    }

    [Fact]
    public async Task RunShellAsync_CapturesOutputAndCompletes()
    {
        var mgr = NewManager();
        var result = await mgr.RunShellAsync("echo shelltask-ok", NewWorkDir(), ShortTimeout);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Detached);
        Assert.Contains("shelltask-ok", result.Stdout);

        var snap = mgr.Get(result.TaskId);
        Assert.NotNull(snap);
        Assert.Equal(TaskRunStatus.Completed, snap!.Status);
        Assert.Equal(TaskKind.Shell, snap.Kind);
        Assert.Contains("shelltask-ok", mgr.TryPeek(result.TaskId, 200) ?? string.Empty);
    }

    [Fact]
    public async Task RunShellAsync_CapturesStdoutSeparately()
    {
        var mgr = NewManager();
        var result = await mgr.RunShellAsync("echo out-marker", NewWorkDir(), ShortTimeout);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("out-marker", result.Stdout);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr));
    }

    [Fact]
    public async Task RunShellAsync_CapturesStderrSeparately()
    {
        var mgr = NewManager();
        var command = OperatingSystem.IsWindows()
            ? "[Console]::Error.WriteLine('err-marker')"
            : "echo err-marker 1>&2";

        var result = await mgr.RunShellAsync(command, NewWorkDir(), ShortTimeout);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("err-marker", result.Stderr);
        Assert.DoesNotContain("err-marker", result.Stdout);
        Assert.Equal(TaskRunStatus.Completed, mgr.Get(result.TaskId)!.Status);
    }

    [Fact]
    public async Task RunShellAsync_NonZeroExit_MarksFailed()
    {
        var mgr = NewManager();
        var result = await mgr.RunShellAsync("exit 3", NewWorkDir(), ShortTimeout);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(TaskRunStatus.Failed, mgr.Get(result.TaskId)!.Status);
    }

    [Fact]
    public async Task RunShellAsync_Timeout_MarksFailedAndTimedOut()
    {
        var mgr = NewManager();
        var result = await mgr.RunShellAsync(SleepCommand(30), NewWorkDir(), TimeSpan.FromSeconds(1));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(TaskRunStatus.Failed, mgr.Get(result.TaskId)!.Status);
    }

    [Fact]
    public async Task RunShellAsync_ExternalCancellation_MarksStopped()
    {
        var mgr = NewManager();
        using var cts = new CancellationTokenSource();
        var run = mgr.RunShellAsync(SleepCommand(30), NewWorkDir(), ShortTimeout, cancellationToken: cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var result = await run;

        Assert.False(result.Detached);
        Assert.Equal(TaskRunStatus.Stopped, mgr.Get(result.TaskId)!.Status);
    }

    [Fact]
    public async Task RunShellAsync_StartFailure_MarksFailed()
    {
        var mgr = NewManager();
        var bogus = Path.Combine(NewWorkDir(), "does-not-exist-" + Guid.NewGuid().ToString("N"));

        var result = await mgr.RunShellAsync("echo hi", bogus, ShortTimeout);

        Assert.False(result.Detached);
        Assert.Equal(TaskRunStatus.Failed, mgr.Get(result.TaskId)!.Status);
    }

    [Fact]
    public async Task RunShellAsync_ClosesLog_AndAdvancesVersion_OnTerminal()
    {
        var mgr = NewManager();
        var result = await mgr.RunShellAsync("echo versioned-output", NewWorkDir(), ShortTimeout);

        var snap = mgr.Get(result.TaskId)!;
        Assert.Equal(TaskRunStatus.Completed, snap.Status);
        // At least one output append (version bump) plus the terminal status transition.
        Assert.True(snap.Version >= 2, $"expected version >= 2, was {snap.Version}");
        // The terminal hook closes and removes the log writer.
        Assert.False(mgr.HasLogWriter(result.TaskId));
    }

    [Fact]
    public async Task RunShellAsync_AllowedDeeperThanSubagentMax()
    {
        var mgr = NewManager();
        // Build a parent chain of shell tasks deeper than MaxSubagentDepth (2). Shells are leaf
        // work and must be allowed deeper than the subagent nesting cap.
        var p1 = mgr.Register(TaskKind.Shell, "p1", parentTaskId: null);   // depth 1
        var p2 = mgr.Register(TaskKind.Shell, "p2", parentTaskId: p1.Id);  // depth 2
        var p3 = mgr.Register(TaskKind.Shell, "p3", parentTaskId: p2.Id);  // depth 3

        var result = await mgr.RunShellAsync("echo deep-leaf", NewWorkDir(), ShortTimeout, parentTaskId: p3.Id);

        var snap = mgr.Get(result.TaskId)!;
        Assert.Equal(4, snap.Depth);
        Assert.Equal(p3.Id, snap.ParentId);
        Assert.Contains("deep-leaf", result.Stdout);
    }

    [Fact]
    public async Task StartShellBackground_ReturnsIdAndCompletes()
    {
        var mgr = NewManager();
        var id = mgr.StartShellBackground("echo bg-shell-ok", NewWorkDir(), ShortTimeout);

        await WaitForStatus(mgr, id, TaskRunStatus.Completed);
        Assert.Contains("bg-shell-ok", mgr.TryPeek(id, 200) ?? string.Empty);
    }

    [Fact]
    public async Task StartShellBackground_CanBeStopped()
    {
        var mgr = NewManager();
        var id = mgr.StartShellBackground(SleepCommand(30), NewWorkDir(), ShortTimeout);

        // Give the process a moment to start, then stop it and expect a Stopped terminal status.
        await Task.Delay(200);
        Assert.Equal(TaskActionResult.Ok, mgr.RequestStop(id));
        await WaitForStatus(mgr, id, TaskRunStatus.Stopped);
    }

    [Fact]
    public async Task StartShellBackground_Stop_KillsProcessTree()
    {
        var dir = NewWorkDir();
        var pidFile = Path.Combine(dir, "grandchild.pid");
        var mgr = NewManager();
        var id = mgr.StartShellBackground(GrandchildSpawningCommand(pidFile), dir, ShortTimeout);

        int grandchildPid = 0;
        try
        {
            grandchildPid = await WaitForPidFile(pidFile);
            Assert.True(IsProcessAlive(grandchildPid), "grandchild should be running before stop");

            Assert.Equal(TaskActionResult.Ok, mgr.RequestStop(id));
            await WaitForStatus(mgr, id, TaskRunStatus.Stopped);

            // Tree-kill must reap the grandchild, not just the shell.
            await WaitForProcessGone(grandchildPid);
        }
        finally
        {
            if (grandchildPid > 0 && IsProcessAlive(grandchildPid))
            {
                try { Process.GetProcessById(grandchildPid).Kill(entireProcessTree: true); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    // Spawns a detached grandchild process that sleeps well past the test, records its PID to
    // <paramref name="pidFile"/>, then blocks the shell itself so the whole tree is alive until
    // stopped. Cross-platform: PowerShell Start-Process on Windows, a backgrounded sleep on bash.
    private static string GrandchildSpawningCommand(string pidFile) =>
        OperatingSystem.IsWindows()
            ? $"$gc = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 120' -PassThru -WindowStyle Hidden; " +
              $"Set-Content -LiteralPath '{pidFile}' -Value $gc.Id; Start-Sleep -Seconds 120"
            : $"sleep 120 & echo $! > '{pidFile}'; sleep 120";

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<int> WaitForPidFile(string path)
    {
        for (var i = 0; i < 500; i++)
        {
            if (File.Exists(path))
            {
                var text = (await File.ReadAllTextAsync(path)).Trim();
                if (int.TryParse(text, out var pid) && pid > 0)
                {
                    return pid;
                }
            }

            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"pid file '{path}' was not written in time.");
    }

    private static async Task WaitForProcessGone(int pid)
    {
        for (var i = 0; i < 500; i++)
        {
            if (!IsProcessAlive(pid)) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"process {pid} was still alive after stop.");
    }

    private static async Task WaitForStatus(TaskManager mgr, string id, TaskRunStatus target)
    {
        for (var i = 0; i < 300; i++)
        {
            if (mgr.Get(id)?.Status == target) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"Task {id} did not reach {target} in time.");
    }
}
