using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskToolsCompatibilityTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-compat", logRoot: null);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    private sealed class FakeHost : ISubagentHost
    {
        public Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default)
        {
            sink.OnAssistantText("fake report");
            sink.OnAssistantTextComplete();
            return Task.FromResult("fake report");
        }
    }

    [Fact]
    public void ExistingTools_KeepNamesAndSchemas()
    {
        Assert.Equal("task", new TaskTool().Name);
        Assert.Equal("task_start", new BackgroundTaskStartTool().Name);
        Assert.Equal("task_output", new BackgroundTaskOutputTool().Name);
        Assert.Equal("task_stop", new BackgroundTaskStopTool().Name);
        Assert.Contains("\"task_id\"", new BackgroundTaskOutputTool().InputSchemaJson);
        Assert.Contains("\"task_id\"", new BackgroundTaskStopTool().InputSchemaJson);
    }

    [Fact]
    public async Task TaskStop_Running_ReturnsStoppedMessage()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr };

        var result = await new BackgroundTaskStopTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), ctx, CancellationToken.None);

        Assert.Contains("has been stopped", result.Content);
        // task_stop requests cancellation; a worker acknowledges it asynchronously. For a
        // bare-registered task with no worker, the token is cancelled and the status stays
        // running until acknowledged — this mirrors the preserved task_stop semantics.
        Assert.True(mgr.Find(t.Id)!.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task TaskStop_UnknownId_ReturnsNotFound()
    {
        var mgr = NewManager();
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr };

        var result = await new BackgroundTaskStopTool().ExecuteAsync(
            Input("""{"task_id":"task-9999"}"""), ctx, CancellationToken.None);

        Assert.Contains("not found", result.Content);
    }

    [Fact]
    public async Task TaskStop_Terminal_ReturnsInvalidStateMessage()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        mgr.Complete(t.Id, "done");
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr };

        var result = await new BackgroundTaskStopTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), ctx, CancellationToken.None);

        Assert.Contains("already finished", result.Content);
    }

    [Fact]
    public async Task TaskOutput_StoppedTask_UsesStoppedLabel()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        mgr.AppendOutput(t.Id, "partial output");
        mgr.Stop(t.Id);
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr };

        var result = await new BackgroundTaskOutputTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), ctx, CancellationToken.None);

        Assert.Contains("partial output", result.Content);
        Assert.Contains("[status: stopped]", result.Content);
    }

    [Fact]
    public async Task TaskStart_ReturnsBackgroundTaskId()
    {
        var mgr = NewManager();
        var ctx = new ToolContext(Directory.GetCurrentDirectory())
        {
            Tasks = mgr,
            Subagents = new FakeHost(),
        };

        var result = await new BackgroundTaskStartTool().ExecuteAsync(
            Input("""{"prompt":"do a thing"}"""), ctx, CancellationToken.None);

        Assert.Contains("Started background task", result.Content);
        Assert.Single(mgr.List());
    }

    [Fact]
    public async Task Task_AtMaxDepth_RefusesWithoutRegistering()
    {
        var mgr = NewManager();
        var ctx = new ToolContext(Directory.GetCurrentDirectory())
        {
            Tasks = mgr,
            Subagents = new FakeHost(),
            CurrentDepth = TaskManager.MaxSubagentDepth,
        };

        var result = await new TaskTool().ExecuteAsync(
            Input("""{"description":"x","prompt":"y"}"""), ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("maximum subagent nesting depth", result.Content);
        Assert.Empty(mgr.List());
    }
}
