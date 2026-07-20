using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Tasks;

public class SubagentManagerTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-sa", logRoot: null);

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    /// <summary>Fake host implementing the new ISubagentHost signature; records what it was given.</summary>
    private sealed class FakeHost : ISubagentHost
    {
        private readonly string _output;
        private readonly TaskCompletionSource? _gate;

        public FakeHost(string output, TaskCompletionSource? gate = null)
        {
            _output = output;
            _gate = gate;
        }

        public string? SeenTaskId { get; private set; }
        public int SeenDepth { get; private set; }
        public List<string> SeenSteers { get; } = new();

        public async Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default)
        {
            SeenTaskId = taskId;
            SeenDepth = depth;
            sink.OnAssistantText(_output);
            sink.OnAssistantTextComplete();
            if (_gate is not null)
            {
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            SeenSteers.AddRange(steering.DrainAll());
            return _output;
        }
    }

    [Fact]
    public async Task Foreground_RegistersCompletesAndReturnsReport()
    {
        var mgr = NewManager();
        var host = new FakeHost("subagent report");

        var report = await mgr.RunSubagentForegroundAsync(
            host, "general-purpose", "go", "desc", new NullSink(), parentTaskId: null);

        Assert.Equal("subagent report", report);
        Assert.Equal("task-0001", host.SeenTaskId);
        Assert.Equal(1, host.SeenDepth);

        var snap = mgr.Get("task-0001");
        Assert.NotNull(snap);
        Assert.Equal(TaskRunStatus.Completed, snap!.Status);
        Assert.Equal(TaskKind.Subagent, snap.Kind);
    }

    [Fact]
    public async Task Foreground_StreamsOutputIntoRing()
    {
        var mgr = NewManager();
        var host = new FakeHost("streamed text");
        await mgr.RunSubagentForegroundAsync(host, "general-purpose", "go", "desc", new NullSink(), parentTaskId: null);
        Assert.Contains("streamed text", mgr.TryPeek("task-0001", 100) ?? string.Empty);
    }

    [Fact]
    public async Task Background_ReturnsIdAndEventuallyCompletes()
    {
        var mgr = NewManager();
        var host = new FakeHost("bg result");

        var id = mgr.StartSubagentBackground(host, "general-purpose", "go", "general-purpose", parentTaskId: null);
        Assert.Equal("task-0001", id);

        await WaitForStatus(mgr, id, TaskRunStatus.Completed);
        Assert.Equal(TaskRunStatus.Completed, mgr.Get(id)!.Status);
    }

    [Fact]
    public async Task Steer_DeliversMessageToRunningSubagent()
    {
        var mgr = NewManager();
        var gate = new TaskCompletionSource();
        var host = new FakeHost("bg", gate);

        var id = mgr.StartSubagentBackground(host, "general-purpose", "go", "general-purpose", parentTaskId: null);

        // Wait until the host has started (SeenTaskId set) before steering.
        await WaitUntil(() => host.SeenTaskId is not null);
        Assert.Equal(TaskActionResult.Ok, mgr.Steer(id, "please adjust"));

        gate.SetResult();
        await WaitForStatus(mgr, id, TaskRunStatus.Completed);
        Assert.Contains("please adjust", host.SeenSteers);
    }

    [Fact]
    public void Steer_UnknownId_ReturnsNotFound()
    {
        var mgr = NewManager();
        Assert.Equal(TaskActionResult.NotFound, mgr.Steer("task-9999", "x"));
    }

    [Fact]
    public void Steer_ShellTask_IsRejected()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);
        Assert.Equal(TaskActionResult.Rejected, mgr.Steer(t.Id, "x"));
    }

    [Fact]
    public void Steer_TerminalTask_ReturnsInvalidState()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        t.AttachSteering(new SteeringInbox());
        mgr.Complete(t.Id, "done");
        Assert.Equal(TaskActionResult.InvalidState, mgr.Steer(t.Id, "x"));
    }

    [Fact]
    public void RequestStop_UnknownId_ReturnsNotFound()
    {
        var mgr = NewManager();
        Assert.Equal(TaskActionResult.NotFound, mgr.RequestStop("task-9999"));
    }

    [Fact]
    public void RequestStop_TerminalTask_ReturnsInvalidState()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        mgr.Complete(t.Id, "done");
        Assert.Equal(TaskActionResult.InvalidState, mgr.RequestStop(t.Id));
    }

    [Fact]
    public void SelectChildTools_AtMaxDepth_StripsTaskCreationTools()
    {
        var reg = new ToolRegistry([new TaskTool(), new BackgroundTaskStartTool(), new ReadFileTool()]);
        var stripped = SubagentHost.SelectChildTools(reg, depth: 2);
        var names = stripped.All.Select(t => t.Name).ToList();
        Assert.DoesNotContain("task", names);
        Assert.DoesNotContain("task_start", names);
        Assert.Contains("read_file", names);
    }

    [Fact]
    public void SelectChildTools_BelowMaxDepth_KeepsTaskCreationTools()
    {
        var reg = new ToolRegistry([new TaskTool(), new BackgroundTaskStartTool()]);
        var names = SubagentHost.SelectChildTools(reg, depth: 1).All.Select(t => t.Name).ToList();
        Assert.Contains("task", names);
        Assert.Contains("task_start", names);
    }

    private static async Task WaitForStatus(TaskManager mgr, string id, TaskRunStatus target)
    {
        for (var i = 0; i < 200; i++)
        {
            if (mgr.Get(id)?.Status == target) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"Task {id} did not reach {target} in time.");
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        for (var i = 0; i < 200; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException("Condition not met in time.");
    }
}
