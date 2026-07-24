using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using Coda.Sdk;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// RecordingSink is the production decorator wrapping the serve WireAgentSink. It must forward
/// EVERY IAgentSink event — including OnToolProgress, which is a default-interface method and so
/// is silently dropped if a decorator forgets to override it. That exact gap made the tool
/// heartbeat inert over the real wire; these tests lock the forwarding through the production chain.
/// </summary>
public sealed class RecordingSinkForwardingTests
{
    private sealed class CorrelatedCapturingInner : IAgentSink
    {
        public List<(ToolCallIdentity Identity, string Name, string Input)> Queued { get; } = [];

        public List<(ToolCallIdentity Identity, string Name, string Input)> Calls { get; } = [];

        public List<(ToolCallIdentity Identity, string Name, ToolCallStatus Status)> Statuses { get; } = [];

        public List<(ToolCallIdentity Identity, string Name, ToolResult Result, ToolCallStatus Status)> Results { get; } = [];

        public List<(ToolCallIdentity Identity, string Name, long ElapsedMs)> Progress { get; } = [];

        public List<ToolActivitySummary> Completions { get; } = [];

        public int LegacyCalls { get; private set; }

        public int LegacyResults { get; private set; }

        public int LegacyProgress { get; private set; }

        public void OnAssistantText(string delta) { }

        public void OnAssistantTextComplete() { }

        public void OnToolCall(string toolName, string inputJson) => this.LegacyCalls++;

        public void OnToolResult(string toolName, ToolResult result) => this.LegacyResults++;

        public void OnToolProgress(string toolName, long elapsedMs) => this.LegacyProgress++;

        public void OnError(string message) { }

        public void OnToolQueued(ToolCallIdentity identity, string toolName, string inputJson) =>
            this.Queued.Add((identity, toolName, inputJson));

        public void OnToolCall(ToolCallIdentity identity, string toolName, string inputJson) =>
            this.Calls.Add((identity, toolName, inputJson));

        public void OnToolStatus(ToolCallIdentity identity, string toolName, ToolCallStatus status) =>
            this.Statuses.Add((identity, toolName, status));

        public void OnToolResult(ToolCallIdentity identity, string toolName, ToolResult result, ToolCallStatus status) =>
            this.Results.Add((identity, toolName, result, status));

        public void OnToolProgress(ToolCallIdentity identity, string toolName, long elapsedMs) =>
            this.Progress.Add((identity, toolName, elapsedMs));

        public void OnToolActivityCompleted(ToolActivitySummary summary) =>
            this.Completions.Add(summary);
    }

    private sealed class LegacyCapturingInner : IAgentSink
    {
        public int Calls { get; private set; }

        public int Results { get; private set; }

        public int Progress { get; private set; }

        public void OnAssistantText(string delta) { }

        public void OnAssistantTextComplete() { }

        public void OnToolCall(string toolName, string inputJson) => this.Calls++;

        public void OnToolResult(string toolName, ToolResult result) => this.Results++;

        public void OnToolProgress(string toolName, long elapsedMs) => this.Progress++;

        public void OnError(string message) { }
    }

    private sealed class CapturingInner : IAgentSink
    {
        private readonly List<(string ToolName, long ElapsedMs)> progress = [];
        public List<string> DeliveredIds { get; } = [];

        public IReadOnlyList<(string ToolName, long ElapsedMs)> Progress
        {
            get { lock (this.progress) { return this.progress.ToList(); } }
        }

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }

        public void OnToolProgress(string toolName, long elapsedMs)
        {
            lock (this.progress)
            {
                this.progress.Add((toolName, elapsedMs));
            }
        }

        public void OnSteeringDelivered(IReadOnlyList<string> ids) => this.DeliveredIds.AddRange(ids);
    }

    [Fact]
    public void RecordingSink_forwards_OnToolProgress_to_inner()
    {
        var inner = new CapturingInner();
        IAgentSink recording = new RecordingSink(inner);

        recording.OnToolProgress("run_command", 5_000);

        Assert.Contains(("run_command", 5_000L), inner.Progress);
    }

    [Fact]
    public void RecordingSink_forwards_OnSteeringDelivered_to_inner()
    {
        var inner = new CapturingInner();
        IAgentSink recording = new RecordingSink(inner);

        recording.OnSteeringDelivered(["first", "second"]);

        Assert.Equal(["first", "second"], inner.DeliveredIds);
    }

    [Fact]
    public void Enriched_events_record_and_forward_once_without_legacy_duplicates()
    {
        var inner = new CorrelatedCapturingInner();
        var sink = new RecordingSink(inner);
        var identity = Id("root", "activity", "call-1", "root:root");

        sink.OnToolQueued(identity, "grep", """{"pattern":"one"}""");
        sink.OnToolCall(identity, "grep", """{"pattern":"one"}""");
        sink.OnToolStatus(identity, "grep", ToolCallStatus.Running);
        sink.OnToolProgress(identity, "grep", 42);
        sink.OnToolResult(identity, "grep", new ToolResult("one"), ToolCallStatus.Succeeded);

        var call = Assert.Single(sink.ToolCalls);
        Assert.Equal("root", call.RootTurnId);
        Assert.Equal("activity", call.ActivityId);
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("root:root", call.SourceId);
        Assert.Equal(ToolCallStatus.Succeeded, call.Status);
        Assert.Equal("one", call.Result);
        Assert.False(call.IsError);
        Assert.Single(inner.Queued);
        Assert.Single(inner.Calls);
        Assert.Single(inner.Statuses);
        Assert.Single(inner.Progress);
        Assert.Single(inner.Results);
        Assert.Equal(0, inner.LegacyCalls);
        Assert.Equal(0, inner.LegacyResults);
        Assert.Equal(0, inner.LegacyProgress);
    }

    [Fact]
    public void Same_name_and_call_id_in_different_sources_remain_distinct()
    {
        var sink = new RecordingSink(null);
        var root = Id("root", "activity", "call-1", "root:root");
        var subagent = Id("root", "activity", "call-1", "subagent:research");
        var secondRoot = Id("root", "activity", "call-2", "root:root");

        sink.OnToolQueued(root, "grep", """{"pattern":"root"}""");
        sink.OnToolQueued(subagent, "grep", """{"pattern":"subagent"}""");
        sink.OnToolQueued(secondRoot, "grep", """{"pattern":"other"}""");
        sink.OnToolCall(root, "grep", """{"pattern":"root"}""");
        sink.OnToolResult(root, "grep", new ToolResult("root result"), ToolCallStatus.Succeeded);
        sink.OnToolResult(subagent, "grep", new ToolResult("subagent result", IsError: true), ToolCallStatus.Failed);

        var calls = sink.ToolCalls;
        Assert.Equal(3, calls.Count);
        Assert.Equal("root result", calls.Single(call => call.SourceId == "root:root" && call.CallId == "call-1").Result);
        var subagentCall = calls.Single(call => call.SourceId == "subagent:research" && call.CallId == "call-1");
        Assert.Equal("subagent result", subagentCall.Result);
        Assert.True(subagentCall.IsError);
        Assert.Equal(ToolCallStatus.Failed, subagentCall.Status);
        var untouched = calls.Single(call => call.SourceId == "root:root" && call.CallId == "call-2");
        Assert.Null(untouched.Result);
        Assert.Equal(ToolCallStatus.Pending, untouched.Status);
    }

    [Fact]
    public void Legacy_callbacks_keep_name_based_recording_behavior()
    {
        var inner = new LegacyCapturingInner();
        var sink = new RecordingSink(inner);

        sink.OnToolCall("grep", """{"pattern":"first"}""");
        sink.OnToolCall("grep", """{"pattern":"second"}""");
        sink.OnToolResult("grep", new ToolResult("second result"));

        var calls = sink.ToolCalls;
        Assert.Equal(2, calls.Count);
        Assert.Null(calls[0].Result);
        Assert.Equal("second result", calls[1].Result);
        Assert.All(calls, call =>
        {
            Assert.Null(call.RootTurnId);
            Assert.Null(call.ActivityId);
            Assert.Null(call.CallId);
            Assert.Null(call.SourceId);
            Assert.Null(call.Status);
        });
        Assert.Equal(2, inner.Calls);
        Assert.Equal(1, inner.Results);
        Assert.Null(sink.CompleteActivity(interrupted: false));
    }

    [Fact]
    public void CompleteActivity_is_idempotent_and_finalizes_unresolved_calls()
    {
        var inner = new CorrelatedCapturingInner();
        var sink = new RecordingSink(inner);
        var pending = Id("root", "activity", "pending", "root:root");
        var awaitingApproval = Id("root", "activity", "approval", "root:root");
        var running = Id("root", "activity", "running", "root:root");
        var succeeded = Id("root", "activity", "succeeded", "root:root");

        sink.OnToolQueued(pending, "read_file", "{}");
        sink.OnToolQueued(awaitingApproval, "read_file", "{}");
        sink.OnToolQueued(running, "read_file", "{}");
        sink.OnToolQueued(succeeded, "read_file", "{}");
        sink.OnToolStatus(awaitingApproval, "read_file", ToolCallStatus.AwaitingApproval);
        sink.OnToolStatus(running, "read_file", ToolCallStatus.Running);
        sink.OnToolResult(succeeded, "read_file", new ToolResult("ok"), ToolCallStatus.Succeeded);

        var first = sink.CompleteActivity(interrupted: true);
        var second = sink.CompleteActivity(interrupted: true);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(ToolCallStatus.Skipped, sink.ToolCalls.Single(call => call.CallId == "pending").Status);
        Assert.Equal(ToolCallStatus.Cancelled, sink.ToolCalls.Single(call => call.CallId == "approval").Status);
        Assert.Equal(ToolCallStatus.Cancelled, sink.ToolCalls.Single(call => call.CallId == "running").Status);
        Assert.Equal(ToolCallStatus.Succeeded, sink.ToolCalls.Single(call => call.CallId == "succeeded").Status);
        Assert.Equal(4, first.TotalCalls);
        Assert.Equal(0, first.FailedCalls);
        Assert.Equal(2, first.CancelledCalls);
        Assert.Equal(1, first.SkippedCalls);
        Assert.Equal("read_file", first.HomogeneousToolName);
        Assert.Single(inner.Completions);
        Assert.Same(first, Assert.Single(inner.Completions));
    }

    [Fact]
    public void CompleteActivity_counts_failed_results_and_uses_null_for_mixed_tool_names()
    {
        var sink = new RecordingSink(null);
        var failed = Id("root", "activity", "failed", "root:root");
        var succeeded = Id("root", "activity", "succeeded", "root:root");

        sink.OnToolQueued(failed, "grep", "{}");
        sink.OnToolQueued(succeeded, "read_file", "{}");
        sink.OnToolResult(failed, "grep", new ToolResult("failed", IsError: true), ToolCallStatus.Failed);
        sink.OnToolResult(succeeded, "read_file", new ToolResult("ok"), ToolCallStatus.Succeeded);

        var summary = Assert.IsType<ToolActivitySummary>(sink.CompleteActivity(interrupted: false));

        Assert.Equal(2, summary.TotalCalls);
        Assert.Equal(1, summary.FailedCalls);
        Assert.Equal(0, summary.CancelledCalls);
        Assert.Equal(0, summary.SkippedCalls);
        Assert.Null(summary.HomogeneousToolName);
        var failedCall = sink.ToolCalls.Single(call => call.CallId == "failed");
        Assert.Equal("failed", failedCall.Result);
        Assert.True(failedCall.IsError);
        Assert.Equal(ToolCallStatus.Failed, failedCall.Status);
    }

    [Fact]
    public void Enriched_events_fall_back_to_a_legacy_inner_once()
    {
        var inner = new LegacyCapturingInner();
        var sink = new RecordingSink(inner);
        var identity = Id("root", "activity", "call", "root:root");

        sink.OnToolQueued(identity, "grep", "{}");
        sink.OnToolCall(identity, "grep", "{}");
        sink.OnToolProgress(identity, "grep", 10);
        sink.OnToolResult(identity, "grep", new ToolResult("ok"), ToolCallStatus.Succeeded);

        Assert.Equal(1, inner.Calls);
        Assert.Equal(1, inner.Progress);
        Assert.Equal(1, inner.Results);
        Assert.Single(sink.ToolCalls);
    }

    [Fact]
    public void OnToolActivityCompleted_forwards_the_enriched_summary_once()
    {
        var inner = new CorrelatedCapturingInner();
        var sink = new RecordingSink(inner);
        var summary = new ToolActivitySummary("root", "activity", 1, 0, 0, 0, "grep");

        sink.OnToolActivityCompleted(summary);

        Assert.Same(summary, Assert.Single(inner.Completions));
    }

    private static ToolCallIdentity Id(string rootTurnId, string activityId, string callId, string sourceId) =>
        new(rootTurnId, activityId, callId, sourceId);

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

    private sealed class SlowTool : ITool
    {
        public string Name => "slow";
        public string Description => "slow";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => true;

        public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(60, cancellationToken);
            return new ToolResult("done");
        }
    }

    [Fact]
    public async Task Heartbeat_reaches_inner_through_the_production_RecordingSink_chain()
    {
        // This is the exact production topology (AgentLoop → RecordingSink → WireAgentSink) that the
        // in-isolation tests bypassed, letting the heartbeat silently die in the decorator.
        var turn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", "slow", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var turn2 = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var loop = new AgentLoop(
            new ScriptedClient(turn1, turn2),
            new ToolRegistry([new SlowTool()]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" },
            toolProgressInterval: TimeSpan.FromMilliseconds(10));

        var inner = new CapturingInner();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, new RecordingSink(inner), CancellationToken.None);

        Assert.NotEmpty(inner.Progress);
        Assert.Contains(inner.Progress, p => p.ToolName == "slow");
    }
}
