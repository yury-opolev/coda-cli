using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Tasks;

public class ShellDetachTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-detach", logRoot: null);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    private static string SleepCommand(int seconds) =>
        OperatingSystem.IsWindows() ? $"Start-Sleep -Seconds {seconds}" : $"sleep {seconds}";

    [Fact]
    public async Task RunShellAsync_Detach_ReturnsDetachedAndKeepsTaskAlive()
    {
        var mgr = NewManager();
        var run = Task.Run(() => mgr.RunShellAsync(SleepCommand(30), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60)));

        await WaitUntil(() => mgr.List().Any(t => t.Kind == TaskKind.Shell));
        var id = mgr.List().First(t => t.Kind == TaskKind.Shell).Id;

        Assert.Equal(TaskActionResult.Ok, mgr.TryDetach(id));

        var result = await run;
        Assert.True(result.Detached);
        Assert.Equal(id, result.TaskId);

        // The detached task keeps running in the background; stop it to clean up.
        Assert.Equal(TaskActionResult.Ok, mgr.RequestStop(id));
        await WaitForStatus(mgr, id, TaskRunStatus.Stopped);
    }

    [Fact]
    public void TryDetach_Subagent_IsRejected()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        Assert.Equal(TaskActionResult.Rejected, mgr.TryDetach(t.Id));
    }

    [Fact]
    public void TryDetach_UnknownId_ReturnsNotFound()
    {
        var mgr = NewManager();
        Assert.Equal(TaskActionResult.NotFound, mgr.TryDetach("task-9999"));
    }

    [Fact]
    public void TryDetach_TerminalShell_ReturnsInvalidState()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);
        mgr.Complete(t.Id, "done");
        Assert.Equal(TaskActionResult.InvalidState, mgr.TryDetach(t.Id));
    }

    [Fact]
    public async Task RunCommandTool_RunInBackground_StartsTaskAndReturnsId()
    {
        var mgr = NewManager();
        var tool = new RunCommandTool();
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr };

        var result = await tool.ExecuteAsync(
            Input("""{"command":"echo bg","run_in_background":true}"""), ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("Started background task", result.Content);
        Assert.Single(mgr.List());
    }

    [Fact]
    public async Task RunCommandTool_ViaManager_ReturnsExitCodeAndOutput()
    {
        var mgr = NewManager();
        var tool = new RunCommandTool();
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr };

        var result = await tool.ExecuteAsync(
            Input("""{"command":"echo rc-ok"}"""), ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("rc-ok", result.Content);
        Assert.Contains("exit code: 0", result.Content);
    }

    [Fact]
    public async Task RunCommandTool_NullTasks_FallsBackToDirectExecution()
    {
        var tool = new RunCommandTool();
        var ctx = new ToolContext(Directory.GetCurrentDirectory());

        var result = await tool.ExecuteAsync(
            Input("""{"command":"echo direct-ok"}"""), ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("direct-ok", result.Content);
        Assert.Contains("exit code: 0", result.Content);
    }

    [Fact]
    public async Task RunShellAsync_DetachedShell_SurvivesTurnCancellation()
    {
        var mgr = NewManager();
        using var turn = new CancellationTokenSource();
        var run = Task.Run(() => mgr.RunShellAsync(
            SleepCommand(30), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60), parentTaskId: null, turn.Token));

        await WaitUntil(() => mgr.List().Any(t => t.Kind == TaskKind.Shell));
        var id = mgr.List().First(t => t.Kind == TaskKind.Shell).Id;

        // Promote to the background, then cancel the originating turn.
        Assert.Equal(TaskActionResult.Ok, mgr.TryDetach(id));
        var result = await run;
        Assert.True(result.Detached);

        turn.Cancel();

        // Turn cancellation must NOT reach a detached shell: it keeps running after the turn ends.
        await Task.Delay(200);
        Assert.Equal(TaskRunStatus.Running, mgr.Get(id)!.Status);

        // It remains independently stoppable via its own lifecycle.
        Assert.Equal(TaskActionResult.Ok, mgr.RequestStop(id));
        await WaitForStatus(mgr, id, TaskRunStatus.Stopped);
    }

    [Fact]
    public async Task RunShellAsync_TurnCancelledBeforeDetach_KillsShellAndReportsStopped()
    {
        var mgr = NewManager();
        using var turn = new CancellationTokenSource();
        var run = Task.Run(() => mgr.RunShellAsync(
            SleepCommand(30), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60), parentTaskId: null, turn.Token));

        await WaitUntil(() => mgr.List().Any(t => t.Kind == TaskKind.Shell));
        var id = mgr.List().First(t => t.Kind == TaskKind.Shell).Id;

        // Cancel the turn while the shell is still in the foreground (no detach requested).
        turn.Cancel();

        var result = await run;
        Assert.False(result.Detached);
        await WaitForStatus(mgr, id, TaskRunStatus.Stopped);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        for (var i = 0; i < 300; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException("Condition not met in time.");
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
