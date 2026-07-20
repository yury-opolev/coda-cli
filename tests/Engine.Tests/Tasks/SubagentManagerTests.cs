using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using LlmClient;
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

    /// <summary>A parent sink that records how many times each event reached it.</summary>
    private sealed class RecordingSink : IAgentSink
    {
        public int AssistantText { get; private set; }
        public int AssistantTextComplete { get; private set; }
        public int ToolCall { get; private set; }
        public int ToolResult { get; private set; }
        public int ToolProgress { get; private set; }
        public int Error { get; private set; }
        public int LimitReached { get; private set; }
        public int StopReason { get; private set; }
        public int Usage { get; private set; }

        public void OnAssistantText(string delta) => this.AssistantText++;
        public void OnAssistantTextComplete() => this.AssistantTextComplete++;
        public void OnToolCall(string toolName, string inputPreview) => this.ToolCall++;
        public void OnToolResult(string toolName, ToolResult result) => this.ToolResult++;
        public void OnToolProgress(string toolName, long elapsedMs) => this.ToolProgress++;
        public void OnError(string message) => this.Error++;
        public void OnLimitReached(string kind, string message) => this.LimitReached++;
        public void OnStopReason(string? stopReason) => this.StopReason++;
        public void OnUsage(TokenUsage usage) => this.Usage++;
    }

    /// <summary>A host that emits exactly one of every IAgentSink event before returning.</summary>
    private sealed class EventEmittingHost : ISubagentHost
    {
        public Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default)
        {
            sink.OnAssistantText("hello");
            sink.OnAssistantTextComplete();
            sink.OnToolCall("read_file", "{}");
            sink.OnToolProgress("read_file", 1234);
            sink.OnToolResult("read_file", new ToolResult("ok"));
            sink.OnUsage(new TokenUsage(7, 11));
            sink.OnLimitReached("max_tokens", "truncated");
            sink.OnError("something odd");
            sink.OnStopReason("end_turn");
            return Task.FromResult("done");
        }
    }

    /// <summary>A host that blocks on the cancellation token so the caller can drive stops.</summary>
    private sealed class BlockingHost : ISubagentHost
    {
        private readonly TaskCompletionSource _started = new();

        public Task Started => this._started.Task;

        public async Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default)
        {
            this._started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return "unreachable";
        }
    }

    [Fact]
    public async Task Foreground_ForwardsEveryOptionalSinkEventToParentExactlyOnce()
    {
        var mgr = NewManager();
        var parent = new RecordingSink();

        await mgr.RunSubagentForegroundAsync(
            new EventEmittingHost(), "general-purpose", "go", "desc", parent, parentTaskId: null);

        Assert.Equal(1, parent.AssistantText);
        Assert.Equal(1, parent.AssistantTextComplete);
        Assert.Equal(1, parent.ToolCall);
        Assert.Equal(1, parent.ToolResult);
        Assert.Equal(1, parent.ToolProgress);
        Assert.Equal(1, parent.Error);
        Assert.Equal(1, parent.LimitReached);
        Assert.Equal(1, parent.StopReason);
        Assert.Equal(1, parent.Usage);
    }

    [Fact]
    public async Task Foreground_KeepsLimitStopAndErrorMarkersVisibleInRing()
    {
        var mgr = NewManager();

        await mgr.RunSubagentForegroundAsync(
            new EventEmittingHost(), "general-purpose", "go", "desc", new NullSink(), parentTaskId: null);

        var ring = mgr.TryPeek("task-0001", 4000) ?? string.Empty;
        Assert.Contains("max_tokens", ring);
        Assert.Contains("end_turn", ring);
        Assert.Contains("something odd", ring);
    }

    [Fact]
    public async Task Foreground_CallerCancellation_StopsTaskAndRethrows()
    {
        var mgr = NewManager();
        var host = new BlockingHost();
        using var caller = new CancellationTokenSource();

        var run = mgr.RunSubagentForegroundAsync(
            host, "general-purpose", "go", "desc", new NullSink(), parentTaskId: null, caller.Token);

        await host.Started;
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(TaskRunStatus.Stopped, mgr.Get("task-0001")!.Status);
    }

    [Fact]
    public async Task Foreground_TaskScopedStop_ReturnsCompatibilityResultWithoutThrowing()
    {
        var mgr = NewManager();
        var host = new BlockingHost();

        var run = mgr.RunSubagentForegroundAsync(
            host, "general-purpose", "go", "desc", new NullSink(), parentTaskId: null);

        await host.Started;
        Assert.Equal(TaskActionResult.Ok, mgr.RequestStop("task-0001"));

        var result = await run;
        Assert.Equal("(subagent stopped)", result);
        Assert.Equal(TaskRunStatus.Stopped, mgr.Get("task-0001")!.Status);
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
