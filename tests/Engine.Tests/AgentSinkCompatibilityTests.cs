using Coda.Agent;
using LlmClient;

namespace Engine.Tests;

public sealed class AgentSinkCompatibilityTests
{
    private sealed class LegacySink : IAgentSink
    {
        public int ToolCalls { get; private set; }
        public int ToolProgresses { get; private set; }
        public int ToolResults { get; private set; }

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputJson) => this.ToolCalls++;
        public void OnToolProgress(string toolName, long elapsedMs) => this.ToolProgresses++;
        public void OnToolResult(string toolName, ToolResult result) => this.ToolResults++;
        public void OnError(string message) { }
    }

    [Fact]
    public void CreateRoot_ensure_activity_and_for_call_create_exact_identity()
    {
        var root = ToolActivityContext.CreateRoot();

        Assert.Matches("^[0-9a-f]{32}$", root.RootTurnId);
        Assert.Equal($"root:{root.RootTurnId}", root.SourceId);
        Assert.Null(root.ActivityId);

        var activity = root.EnsureActivity();
        Assert.Matches("^[0-9a-f]{32}$", activity.ActivityId);

        var identity = activity.ForCall("call-1");
        Assert.Equal(new ToolCallIdentity(root.RootTurnId, activity.ActivityId!, "call-1", root.SourceId), identity);
    }

    [Fact]
    public void EnsureActivity_when_already_present_returns_same_value_without_new_id()
    {
        var activity = ToolActivityContext.CreateRoot().EnsureActivity();

        Assert.Same(activity, activity.EnsureActivity());
        Assert.Equal(activity.ActivityId, activity.EnsureActivity().ActivityId);
    }

    [Fact]
    public void ForSubagent_preserves_root_and_activity_and_changes_source()
    {
        var activity = ToolActivityContext.CreateRoot().EnsureActivity();

        var subagent = activity.ForSubagent("research-42");

        Assert.Equal(activity.RootTurnId, subagent.RootTurnId);
        Assert.Equal(activity.ActivityId, subagent.ActivityId);
        Assert.Equal("subagent:research-42", subagent.SourceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void ForSubagent_rejects_blank_task_id(string taskId)
    {
        Assert.Throws<ArgumentException>(() => ToolActivityContext.CreateRoot().ForSubagent(taskId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void ForCall_rejects_blank_call_id(string callId)
    {
        Assert.Throws<ArgumentException>(() => ToolActivityContext.CreateRoot().EnsureActivity().ForCall(callId));
    }

    [Fact]
    public void ForCall_before_activity_throws()
    {
        Assert.Throws<InvalidOperationException>(() => ToolActivityContext.CreateRoot().ForCall("call-1"));
    }

    [Fact]
    public void Summary_cancelled_is_derived_from_cancelled_call_count()
    {
        var cancelled = new ToolActivitySummary("root", "activity", 2, 0, 1, 0, "read");
        var notCancelled = new ToolActivitySummary("root", "activity", 2, 0, 0, 0, "read");

        Assert.True(cancelled.Cancelled);
        Assert.False(notCancelled.Cancelled);
    }

    [Fact]
    public void Enriched_callbacks_forward_to_legacy_implementations_once()
    {
        IAgentSink sink = new LegacySink();
        var legacy = Assert.IsType<LegacySink>(sink);
        var identity = new ToolActivityContext("root", "root:root", "activity").ForCall("call");

        sink.OnToolCall(identity, "read", "{}");
        sink.OnToolProgress(identity, "read", 42);
        sink.OnToolResult(identity, "read", new ToolResult("ok"), ToolCallStatus.Succeeded);

        Assert.Equal(1, legacy.ToolCalls);
        Assert.Equal(1, legacy.ToolProgresses);
        Assert.Equal(1, legacy.ToolResults);
    }

    [Fact]
    public void New_default_callbacks_are_no_ops_for_legacy_only_sink()
    {
        IAgentSink sink = new LegacySink();
        var legacy = Assert.IsType<LegacySink>(sink);
        var identity = new ToolActivityContext("root", "root:root", "activity").ForCall("call");

        sink.OnToolQueued(identity, "read", "{}");
        sink.OnToolStatus(identity, "read", ToolCallStatus.Pending);
        sink.OnToolActivityCompleted(new ToolActivitySummary("root", "activity", 1, 0, 0, 0, "read"));

        Assert.Equal(0, legacy.ToolCalls);
        Assert.Equal(0, legacy.ToolProgresses);
        Assert.Equal(0, legacy.ToolResults);
    }
}
