using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Tasks;

public class ParityToolsTests : IDisposable
{
    // Hermetic log root so TaskRemove log-preservation assertions don't touch ~/.coda.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coda-parity-" + Guid.NewGuid().ToString("N"));
    public ParityToolsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ } }

    private TaskManager NewManager() => new(sessionId: "sess-parity", logRoot: _dir);
    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;
    private static ToolContext Ctx(TaskManager mgr, string? callerTaskId = null) =>
        new(Directory.GetCurrentDirectory()) { Tasks = mgr, CurrentTaskId = callerTaskId };

    [Fact]
    public async Task TaskWait_TerminalTask_ReportsStatus()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        mgr.Complete(t.Id, "done");

        var result = await new TaskWaitTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("completed", result.Content);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task TaskWait_Timeout_ReportsStillRunning_WithoutStopping()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);

        var result = await new TaskWaitTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}","timeout_seconds":1}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("still running", result.Content);
        Assert.Equal(TaskRunStatus.Running, mgr.Get(t.Id)!.Status); // not stopped by the timeout
    }

    [Fact]
    public async Task TaskWait_UnauthorizedTask_IsIndistinguishableFromNotFound()
    {
        var mgr = NewManager();
        var a = mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        var b = mgr.Register(TaskKind.Subagent, "b", parentTaskId: null);

        var result = await new TaskWaitTool().ExecuteAsync(
            Input($$"""{"task_id":"{{b.Id}}","timeout_seconds":30}"""), Ctx(mgr, callerTaskId: a.Id), CancellationToken.None);

        Assert.Contains("not found", result.Content);
    }

    [Fact]
    public async Task TaskWait_TurnCancellation_Propagates()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        using var cts = new CancellationTokenSource();
        var run = new TaskWaitTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}","timeout_seconds":300}"""), Ctx(mgr), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
    }

    [Fact]
    public async Task TaskBackground_ForegroundShell_MovesToBackground()
    {
        var mgr = NewManager();
        var shell = mgr.Register(TaskKind.Shell, "build", parentTaskId: null); // Foreground by default

        var result = await new TaskBackgroundTool().ExecuteAsync(
            Input($$"""{"task_id":"{{shell.Id}}"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("background", result.Content);
        Assert.Equal(TaskExecutionMode.Background, mgr.Get(shell.Id)!.Mode);
    }

    [Fact]
    public async Task TaskBackground_Subagent_IsRejected()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);

        var result = await new TaskBackgroundTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("not a shell", result.Content);
    }

    [Fact]
    public async Task TaskRemove_TerminalTask_RemovesAndKeepsLog()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        var logPath = t.LogPath;
        mgr.AppendOutput(t.Id, "log line");
        mgr.Complete(t.Id, "done");

        var result = await new TaskRemoveTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("removed", result.Content);
        Assert.Null(mgr.Get(t.Id));
        Assert.True(File.Exists(logPath));
    }

    [Fact]
    public async Task TaskRemove_RunningTask_IsRejected()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);

        var result = await new TaskRemoveTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("still running", result.Content);
    }

    [Fact]
    public void BuiltInTools_RegistersNewParityTools()
    {
        var names = BuiltInTools.All().Select(t => t.Name).ToList();

        Assert.Contains("task_background", names);
        Assert.Contains("task_wait", names);
        Assert.Contains("task_remove", names);
        // The spawn tool `task` is parent-only (added by TurnPipelineBuilder), never a built-in.
        Assert.DoesNotContain("task", names);
    }
}
