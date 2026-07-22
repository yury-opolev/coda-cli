using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Tasks;

public class NewTaskToolsTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-newtools", logRoot: null);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolContext Ctx(TaskManager mgr, string? callerTaskId = null) =>
        new(Directory.GetCurrentDirectory()) { Tasks = mgr, CurrentTaskId = callerTaskId };

    [Fact]
    public async Task TaskList_Empty_ReportsNoTasks()
    {
        var mgr = NewManager();
        var result = await new TaskListTool().ExecuteAsync(Input("{}"), Ctx(mgr), CancellationToken.None);
        Assert.Contains("No tasks", result.Content);
    }

    [Fact]
    public async Task TaskList_ListsRegisteredTasks()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "build the app", parentTaskId: null);

        var result = await new TaskListTool().ExecuteAsync(Input("{}"), Ctx(mgr), CancellationToken.None);

        Assert.Contains(t.Id, result.Content);
        Assert.Contains("shell", result.Content);
        Assert.Contains("running", result.Content);
    }

    [Fact]
    public async Task TaskGet_UnknownId_ReportsNotFound()
    {
        var mgr = NewManager();
        var result = await new TaskGetTool().ExecuteAsync(
            Input("""{"task_id":"task-9999"}"""), Ctx(mgr), CancellationToken.None);
        Assert.Contains("not found", result.Content);
    }

    [Fact]
    public async Task TaskGet_KnownId_ReportsStatus()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "research", parentTaskId: null);

        var result = await new TaskGetTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("status: running", result.Content);
        Assert.Contains("kind: subagent", result.Content);
    }

    [Fact]
    public async Task TaskPeek_ReturnsRecentOutputWithoutConsuming()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        mgr.AppendOutput(t.Id, "peek-me-please");

        var result = await new TaskPeekTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("peek-me-please", result.Content);

        // Peeking did not advance the incremental cursor, so task_output still returns the text.
        var (found, text, _, _) = mgr.ReadForMainAgent(t.Id);
        Assert.True(found);
        Assert.Contains("peek-me-please", text);
    }

    [Fact]
    public async Task TaskSend_RunningSubagent_DeliversMessage()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        t.AttachSteering(new SteeringInbox());

        var result = await new TaskSendTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}","message":"tweak the plan"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("delivered", result.Content);
        Assert.Contains("tweak the plan", t.Steering!.DrainAll());
    }

    [Fact]
    public async Task Task_recall_returns_pending_messages_in_order_and_clears_them()
    {
        var mgr = NewManager();
        var task = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        task.AttachSteering(new SteeringInbox());
        task.Steering!.Enqueue("first");
        task.Steering.Enqueue("second");

        var result = await new TaskRecallTool().ExecuteAsync(
            Input($$"""{"task_id":"{{task.Id}}"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("- first", result.Content);
        Assert.Contains("- second", result.Content);
        Assert.Empty(task.Steering.RecallAll());
    }

    [Fact]
    public async Task Task_recall_unauthorized_target_is_indistinguishable_from_not_found()
    {
        var mgr = NewManager();
        var branchA = mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        var branchB = mgr.Register(TaskKind.Subagent, "b", parentTaskId: null);
        branchB.AttachSteering(new SteeringInbox());
        branchB.Steering!.Enqueue("secret");

        var result = await new TaskRecallTool().ExecuteAsync(
            Input($$"""{"task_id":"{{branchB.Id}}"}"""), Ctx(mgr, branchA.Id), CancellationToken.None);

        Assert.Equal($"Task '{branchB.Id}' not found.", result.Content);
        Assert.Single(branchB.Steering.RecallAll());
    }

    [Fact]
    public async Task TaskSend_ShellTask_IsRejected()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);

        var result = await new TaskSendTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}","message":"x"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("cannot be steered", result.Content);
    }

    // ─── Security scope: main sees all, subagent sees only descendants ──────────

    [Fact]
    public async Task TaskList_Main_SeesEveryTask()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "parent", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "child", parentTaskId: parent.Id);
        var other = mgr.Register(TaskKind.Subagent, "other", parentTaskId: null);

        var result = await new TaskListTool().ExecuteAsync(Input("{}"), Ctx(mgr, callerTaskId: null), CancellationToken.None);

        Assert.Contains(parent.Id, result.Content);
        Assert.Contains(child.Id, result.Content);
        Assert.Contains(other.Id, result.Content);
    }

    [Fact]
    public async Task TaskList_Subagent_SeesOnlyItsDescendants()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "parent", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "child", parentTaskId: parent.Id);
        var other = mgr.Register(TaskKind.Subagent, "other", parentTaskId: null);

        var result = await new TaskListTool().ExecuteAsync(
            Input("{}"), Ctx(mgr, callerTaskId: parent.Id), CancellationToken.None);

        Assert.Contains(child.Id, result.Content);
        // A subagent never sees itself, its ancestors, or unrelated branches.
        Assert.DoesNotContain(parent.Id, result.Content);
        Assert.DoesNotContain(other.Id, result.Content);
    }

    [Fact]
    public async Task TaskGet_UnauthorizedTask_IsIndistinguishableFromNotFound()
    {
        var mgr = NewManager();
        var branchA = mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        var branchB = mgr.Register(TaskKind.Subagent, "b", parentTaskId: null);

        var result = await new TaskGetTool().ExecuteAsync(
            Input($$"""{"task_id":"{{branchB.Id}}"}"""), Ctx(mgr, callerTaskId: branchA.Id), CancellationToken.None);

        Assert.Contains("not found", result.Content);
        // No status/kind detail leaks for an unauthorized target.
        Assert.DoesNotContain("status:", result.Content);
    }

    [Fact]
    public async Task TaskPeek_UnauthorizedTask_IsIndistinguishableFromNotFound()
    {
        var mgr = NewManager();
        var branchA = mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        var branchB = mgr.Register(TaskKind.Subagent, "b", parentTaskId: null);
        mgr.AppendOutput(branchB.Id, "secret output");

        var result = await new TaskPeekTool().ExecuteAsync(
            Input($$"""{"task_id":"{{branchB.Id}}"}"""), Ctx(mgr, callerTaskId: branchA.Id), CancellationToken.None);

        Assert.Contains("not found", result.Content);
        Assert.DoesNotContain("secret output", result.Content);
    }

    [Fact]
    public async Task TaskSend_UnauthorizedSubagent_IsIndistinguishableFromNotFound()
    {
        var mgr = NewManager();
        var branchA = mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        var branchB = mgr.Register(TaskKind.Subagent, "b", parentTaskId: null);
        branchB.AttachSteering(new SteeringInbox());

        var result = await new TaskSendTool().ExecuteAsync(
            Input($$"""{"task_id":"{{branchB.Id}}","message":"do this"}"""),
            Ctx(mgr, callerTaskId: branchA.Id), CancellationToken.None);

        Assert.Contains("not found", result.Content);
        // The steering message never reached the unauthorized target.
        Assert.Empty(branchB.Steering!.DrainAll());
    }

    [Fact]
    public async Task TaskSend_DescendantSubagent_DeliversMessage()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "parent", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "child", parentTaskId: parent.Id);
        child.AttachSteering(new SteeringInbox());

        var result = await new TaskSendTool().ExecuteAsync(
            Input($$"""{"task_id":"{{child.Id}}","message":"refine"}"""),
            Ctx(mgr, callerTaskId: parent.Id), CancellationToken.None);

        Assert.Contains("delivered", result.Content);
        Assert.Contains("refine", child.Steering!.DrainAll());
    }
}
