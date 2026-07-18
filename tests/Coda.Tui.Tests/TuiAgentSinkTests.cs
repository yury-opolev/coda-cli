using Coda.Agent;
using Coda.Tui.Agent;
using Coda.Tui.Ui.Events;
using LlmClient;

namespace Coda.Tui.Tests;

/// <summary>
/// The <see cref="TuiAgentSink"/> is a pure adapter from <see cref="IAgentSink"/> callbacks to
/// semantic <see cref="UiEvent"/>s: every callback publishes exactly one matching event, in order,
/// with the payload passed through verbatim. It renders nothing itself (no Spectre, no truncation).
/// </summary>
public sealed class TuiAgentSinkTests
{
    [Fact]
    public void Constructor_rejects_a_null_publisher()
    {
        Assert.Throws<ArgumentNullException>(() => new TuiAgentSink(null!));
    }

    [Fact]
    public void Sink_forwards_every_IAgentSink_event_once_and_in_order()
    {
        var events = new List<UiEvent>();
        var sink = new TuiAgentSink(new CollectingPublisher(events));
        var result = new ToolResult("ok", false);

        sink.OnAssistantText("a");
        sink.OnAssistantTextComplete();
        sink.OnToolCall("grep", "{}");
        sink.OnToolProgress("grep", 1234);
        sink.OnToolResult("grep", result);
        sink.OnUsage(new TokenUsage(10, 2));
        sink.OnStopReason("end_turn");
        sink.OnLimitReached("max_tokens", "limit");
        sink.OnError("boom");

        Assert.Collection(
            events,
            item => Assert.IsType<AssistantTextDeltaEvent>(item),
            item => Assert.IsType<AssistantTextCompletedEvent>(item),
            item => Assert.IsType<ToolStartedEvent>(item),
            item => Assert.IsType<ToolProgressEvent>(item),
            item => Assert.IsType<ToolCompletedEvent>(item),
            item => Assert.IsType<UsageEvent>(item),
            item => Assert.IsType<StopReasonEvent>(item),
            item => Assert.IsType<LimitReachedEvent>(item),
            item => Assert.IsType<AgentErrorEvent>(item));
    }

    [Fact]
    public void Sink_passes_payloads_through_verbatim()
    {
        var events = new List<UiEvent>();
        var sink = new TuiAgentSink(new CollectingPublisher(events));
        var result = new ToolResult("tool output", IsError: true);
        var usage = new TokenUsage(10, 2);

        sink.OnAssistantText("hello");
        sink.OnToolCall("grep", "{\"q\":1}");
        sink.OnToolProgress("grep", 1234);
        sink.OnToolResult("grep", result);
        sink.OnUsage(usage);
        sink.OnStopReason("end_turn");
        sink.OnLimitReached("max_tokens", "limit hit");
        sink.OnError("boom");

        Assert.Equal("hello", Assert.IsType<AssistantTextDeltaEvent>(events[0]).Delta);

        var started = Assert.IsType<ToolStartedEvent>(events[1]);
        Assert.Equal("grep", started.ToolName);
        Assert.Equal("{\"q\":1}", started.InputJson);

        var progress = Assert.IsType<ToolProgressEvent>(events[2]);
        Assert.Equal("grep", progress.ToolName);
        Assert.Equal(1234, progress.ElapsedMs);

        var completed = Assert.IsType<ToolCompletedEvent>(events[3]);
        Assert.Equal("grep", completed.ToolName);
        Assert.Same(result, completed.Result);

        Assert.Equal(usage, Assert.IsType<UsageEvent>(events[4]).Usage);
        Assert.Equal("end_turn", Assert.IsType<StopReasonEvent>(events[5]).StopReason);

        var limit = Assert.IsType<LimitReachedEvent>(events[6]);
        Assert.Equal("max_tokens", limit.Kind);
        Assert.Equal("limit hit", limit.Message);

        Assert.Equal("boom", Assert.IsType<AgentErrorEvent>(events[7]).Message);
    }

    private sealed class CollectingPublisher(List<UiEvent> events) : IUiEventPublisher
    {
        public void Publish(UiEvent uiEvent) => events.Add(uiEvent);
    }
}
