using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Coda.Sdk;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Regression coverage for the real foreground-subagent forwarding pipeline:
/// parent <see cref="AgentLoop"/> → <c>task</c> → <see cref="SubagentHost"/> → child
/// <see cref="AgentLoop"/> → <c>CollectingSink</c> → <c>TaskOutputSink</c>. The parent root
/// recording sink must receive every child event with its source identity intact.
/// </summary>
public sealed class SubagentForwardingTests
{
    /// <summary>Yields pre-baked turns in order across successive StreamAsync calls.</summary>
    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = turns[this.turn++];
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    /// <summary>
    /// A read-only tool that blocks until <paramref name="gate"/> is released, so the child loop's
    /// tool-progress heartbeat is guaranteed to pulse. Reusing it in the parent also exercises a
    /// provider call id/name collision across the root and child sources.
    /// </summary>
    private sealed class ProbeTool(TaskCompletionSource? gate = null) : ITool
    {
        public string Name => "probe";
        public string Description => "blocks until released";
        public string InputSchemaJson => """{"type":"object"}""";
        public bool IsReadOnly => true;

        public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            if (gate is not null)
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new ToolResult("probe done");
        }
    }

    private sealed class CapturingSink(TaskCompletionSource gate) : IAgentSink
    {
        public int UsageCalls { get; private set; }
        public TokenUsage TotalUsage { get; private set; } = TokenUsage.Zero;
        public List<(ToolCallIdentity Identity, string ToolName, string Input)> Queued { get; } = [];
        public List<(ToolCallIdentity Identity, string ToolName, string Input)> Calls { get; } = [];
        public List<(ToolCallIdentity Identity, string ToolName, ToolCallStatus Status)> Statuses { get; } = [];
        public List<(ToolCallIdentity Identity, string ToolName, long ElapsedMs)> Progress { get; } = [];
        public List<(ToolCallIdentity Identity, string ToolName, ToolResult Result, ToolCallStatus Status)> Results { get; } = [];
        public List<ToolActivitySummary> Completions { get; } = [];
        public List<(string ToolName, string Input)> LegacyCalls { get; } = [];
        public List<(string ToolName, ToolResult Result)> LegacyResults { get; } = [];
        public List<(string ToolName, long ElapsedMs)> LegacyProgress { get; } = [];

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) => this.LegacyCalls.Add((toolName, inputPreview));
        public void OnToolResult(string toolName, ToolResult result) => this.LegacyResults.Add((toolName, result));
        public void OnError(string message) { }
        public void OnLimitReached(string kind, string message) { }
        public void OnStopReason(string? stopReason) { }

        public void OnToolProgress(string toolName, long elapsedMs)
        {
            this.LegacyProgress.Add((toolName, elapsedMs));
            gate.TrySetResult();
        }

        public void OnToolQueued(ToolCallIdentity identity, string toolName, string inputJson) =>
            this.Queued.Add((identity, toolName, inputJson));

        public void OnToolCall(ToolCallIdentity identity, string toolName, string inputJson) =>
            this.Calls.Add((identity, toolName, inputJson));

        public void OnToolStatus(ToolCallIdentity identity, string toolName, ToolCallStatus status) =>
            this.Statuses.Add((identity, toolName, status));

        public void OnToolProgress(ToolCallIdentity identity, string toolName, long elapsedMs)
        {
            this.Progress.Add((identity, toolName, elapsedMs));
            gate.TrySetResult();
        }

        public void OnToolResult(ToolCallIdentity identity, string toolName, ToolResult result, ToolCallStatus status) =>
            this.Results.Add((identity, toolName, result, status));

        public void OnToolActivityCompleted(ToolActivitySummary summary) => this.Completions.Add(summary);

        public void OnUsage(TokenUsage usage)
        {
            this.UsageCalls++;
            this.TotalUsage = this.TotalUsage.Add(usage);
        }
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolQueued(ToolCallIdentity identity, string toolName, string inputJson) { }
        public void OnToolCall(ToolCallIdentity identity, string toolName, string inputJson) { }
        public void OnToolStatus(ToolCallIdentity identity, string toolName, ToolCallStatus status) { }
        public void OnToolProgress(string toolName, long elapsedMs) { }
        public void OnToolProgress(ToolCallIdentity identity, string toolName, long elapsedMs) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnToolResult(ToolCallIdentity identity, string toolName, ToolResult result, ToolCallStatus status) { }
        public void OnToolActivityCompleted(ToolActivitySummary summary) { }
        public void OnError(string message) { }
    }

    /// <summary>Deliberately implements only the original host contract.</summary>
    private sealed class LegacyHost : ISubagentHost
    {
        public int Calls { get; private set; }

        public Task<string> RunSubagentAsync(
            string subagentType,
            string prompt,
            IAgentSink sink,
            SteeringInbox steering,
            string taskId,
            int depth,
            CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return Task.FromResult("legacy report");
        }
    }

    private static AgentOptions Options() =>
        new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" };

    [Fact]
    public async Task Foreground_subagent_forwards_source_aware_events_to_the_parent_once()
    {
        var subagentUsage = new TokenUsage(31, 17);
        var rootActivity = new ToolActivityContext("root-turn", "root:root-turn", "activity");

        // Parent turn 1 delegates with t1 and then executes a probe with s1. The child deliberately
        // reuses probe/s1 so RecordingSink must distinguish the two by SourceId.
        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("t1", "task", """{"description":"probe","prompt":"probe"}""")),
            AssistantStreamEvent.Tool(new ToolUseBlock("s1", "probe", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        // Child turn 1 -> the same provider call id and tool name as the parent's probe.
        var subagentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("s1", "probe", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        // Child turn 2 -> emit the only usage in the whole run.
        var subagentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("subagent report"),
            AssistantStreamEvent.Finished("end_turn", subagentUsage),
        };
        // Parent turn 2 -> wrap up (still no usage).
        var parentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("all done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var client = new ScriptedClient(parentTurn1, subagentTurn1, subagentTurn2, parentTurn2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ProbeTool(gate);
        var subagentTools = new ToolRegistry([probe]);
        var mgr = new TaskManager(sessionId: "subagent-forwarding", logRoot: null);
        var host = new SubagentHost(
            client,
            subagentTools,
            new AllowAllPermissionPrompt(),
            Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            // A short heartbeat so the nested loop pulses within the test; large enough that only one
            // pulse fires before the gate-released tool returns and the heartbeat is torn down.
            toolProgressInterval: TimeSpan.FromMilliseconds(75));

        var parentTools = new ToolRegistry([new TaskTool(), probe]);
        var loop = new AgentLoop(
            client,
            parentTools,
            new AllowAllPermissionPrompt(),
            Options(),
            host,
            tasks: mgr,
            toolActivity: rootActivity);
        var sink = new CapturingSink(gate);
        var rootRecording = new RecordingSink(sink);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, rootRecording, CancellationToken.None);

        var task = Assert.Single(mgr.List());
        var parentTask = rootActivity.ForCall("t1");
        var parentProbe = rootActivity.ForCall("s1");
        var childProbe = rootActivity.ForSubagent(task.Id).ForCall("s1");

        Assert.Equal($"subagent:{task.Id}", childProbe.SourceId);
        Assert.Equal(rootActivity.RootTurnId, childProbe.RootTurnId);
        Assert.Equal(rootActivity.ActivityId, childProbe.ActivityId);
        Assert.Equal("s1", childProbe.CallId);
        Assert.NotEqual(parentProbe, childProbe);
        Assert.Equal(parentProbe.CallId, childProbe.CallId);

        AssertIdentitiesExactlyOnce(sink.Queued.Select(e => e.Identity), parentTask, parentProbe, childProbe);
        AssertIdentitiesExactlyOnce(sink.Calls.Select(e => e.Identity), parentTask, parentProbe, childProbe);
        AssertIdentitiesExactlyOnce(
            sink.Statuses.Where(e => e.Status == ToolCallStatus.Running).Select(e => e.Identity),
            parentTask,
            parentProbe,
            childProbe);
        AssertIdentitiesExactlyOnce(sink.Results.Select(e => e.Identity), parentTask, parentProbe, childProbe);

        var progress = Assert.Single(sink.Progress);
        Assert.Equal(childProbe, progress.Identity);
        Assert.Equal("probe", progress.ToolName);
        Assert.Empty(sink.LegacyCalls);
        Assert.Empty(sink.LegacyResults);
        Assert.Empty(sink.LegacyProgress);

        // Usage is not identity-bearing, but it must retain its existing exactly-once semantics.
        Assert.Equal(1, sink.UsageCalls);
        Assert.Equal(subagentUsage, sink.TotalUsage);

        // Child loops never complete the root activity. The root RecordingSink emits the one
        // completion at the parent boundary and is idempotent if the caller's cleanup runs twice.
        Assert.Empty(sink.Completions);
        var completion = Assert.IsType<ToolActivitySummary>(rootRecording.CompleteActivity(interrupted: false));
        Assert.Same(completion, rootRecording.CompleteActivity(interrupted: false));
        Assert.Same(completion, Assert.Single(sink.Completions));
        Assert.Equal(rootActivity.RootTurnId, completion.RootTurnId);
        Assert.Equal(rootActivity.ActivityId, completion.ActivityId);
        Assert.Equal(3, completion.TotalCalls);
    }

    [Fact]
    public async Task Enriched_host_overload_delegates_to_a_legacy_host_implementation()
    {
        ISubagentHost host = new LegacyHost();
        var legacy = Assert.IsType<LegacyHost>(host);

        var report = await host.RunSubagentAsync(
            "general-purpose",
            "do it",
            new NullSink(),
            new SteeringInbox(),
            "task-legacy",
            depth: 1,
            parentActivity: new ToolActivityContext("root", "root:root", "activity"),
            cancellationToken: CancellationToken.None);

        Assert.Equal("legacy report", report);
        Assert.Equal(1, legacy.Calls);
    }

    [Fact]
    public async Task Standalone_subagent_without_parent_activity_creates_its_own_root()
    {
        var firstTurn = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("s1", "probe", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var secondTurn = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };
        var client = new ScriptedClient(firstTurn, secondTurn);
        var host = new SubagentHost(
            client,
            new ToolRegistry([new ProbeTool()]),
            new AllowAllPermissionPrompt(),
            Options(),
            new TaskManager(sessionId: "standalone-subagent", logRoot: null),
            includeAnthropicSystemPrefix: false);
        var sink = new CapturingSink(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        await host.RunSubagentAsync(
            "general-purpose",
            "probe",
            sink,
            new SteeringInbox(),
            "task-standalone",
            depth: 1,
            parentActivity: null,
            cancellationToken: CancellationToken.None);

        var identity = Assert.Single(sink.Queued).Identity;
        Assert.Equal($"root:{identity.RootTurnId}", identity.SourceId);
        Assert.NotEqual("subagent:task-standalone", identity.SourceId);
        Assert.Equal("s1", identity.CallId);
    }

    private static void AssertIdentitiesExactlyOnce(
        IEnumerable<ToolCallIdentity> actual,
        params ToolCallIdentity[] expected)
    {
        var identities = actual.ToArray();
        Assert.Equal(expected.Length, identities.Length);
        foreach (var identity in expected)
        {
            Assert.Single(identities, candidate => candidate == identity);
        }
    }
}
