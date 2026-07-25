using Coda.Agent;
using Coda.Tui.Agent;
using Coda.Tui.Ui.Events;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests that TuiAgentSink correctly maps OnThinking / OnThinkingComplete sink callbacks to
/// ThinkingDeltaEvent / ThinkingCompleteEvent UI events, with the injected TimeProvider used for
/// timing so tests are deterministic.
/// </summary>
public sealed class TuiAgentSinkThinkingTests
{
    private sealed class CollectingPublisher(List<UiEvent> events) : IUiEventPublisher
    {
        public void Publish(UiEvent uiEvent) => events.Add(uiEvent);
    }

    [Fact]
    public void OnThinking_publishes_ThinkingDeltaEvent_with_delta()
    {
        var events = new List<UiEvent>();
        var tp = new ManualTimeProvider();
        IAgentSink sink = new TuiAgentSink(new CollectingPublisher(events), tp);

        sink.OnThinking("reasoning text");

        var evt = Assert.IsType<ThinkingDeltaEvent>(Assert.Single(events));
        Assert.Equal("reasoning text", evt.Delta);
    }

    [Fact]
    public void OnThinkingComplete_after_OnThinking_publishes_ThinkingCompleteEvent_with_elapsed()
    {
        var events = new List<UiEvent>();
        var tp = new ManualTimeProvider();
        IAgentSink sink = new TuiAgentSink(new CollectingPublisher(events), tp);

        tp.Advance(TimeSpan.Zero);           // t=0
        sink.OnThinking("start");            // burst starts at t=0
        tp.Advance(TimeSpan.FromSeconds(3)); // t=3s
        sink.OnThinkingComplete();

        var complete = Assert.IsType<ThinkingCompleteEvent>(events[^1]);
        Assert.Equal(3000L, complete.ElapsedMs);
    }

    [Fact]
    public void BurstStartedAt_captured_on_first_delta_reused_for_subsequent_deltas()
    {
        var events = new List<UiEvent>();
        var tp = new ManualTimeProvider();
        IAgentSink sink = new TuiAgentSink(new CollectingPublisher(events), tp);

        tp.Advance(TimeSpan.Zero);                   // t=0, first delta
        sink.OnThinking("a");
        tp.Advance(TimeSpan.FromMilliseconds(100));  // t=100ms, second delta
        sink.OnThinking("b");

        var deltas = events.OfType<ThinkingDeltaEvent>().ToList();
        Assert.Equal(2, deltas.Count);
        // Both deltas should carry the SAME BurstStartedAt (captured at the first delta).
        Assert.Equal(deltas[0].BurstStartedAt, deltas[1].BurstStartedAt);
    }

    [Fact]
    public void Second_burst_gets_independent_timing_after_first_completes()
    {
        var events = new List<UiEvent>();
        var tp = new ManualTimeProvider();
        IAgentSink sink = new TuiAgentSink(new CollectingPublisher(events), tp);

        // First burst: 0 → 200ms
        tp.Advance(TimeSpan.Zero);
        sink.OnThinking("burst1");
        tp.Advance(TimeSpan.FromMilliseconds(200));
        sink.OnThinkingComplete();

        // Second burst: 200 → 700ms → elapsed = 500ms
        sink.OnThinking("burst2");
        tp.Advance(TimeSpan.FromMilliseconds(500));
        sink.OnThinkingComplete();

        var completes = events.OfType<ThinkingCompleteEvent>().ToList();
        Assert.Equal(2, completes.Count);
        Assert.Equal(200L, completes[0].ElapsedMs);
        Assert.Equal(500L, completes[1].ElapsedMs);
    }

    [Fact]
    public void Null_TimeProvider_uses_system_clock_without_throwing()
    {
        var events = new List<UiEvent>();
        IAgentSink sink = new TuiAgentSink(new CollectingPublisher(events)); // no TimeProvider

        sink.OnThinking("hello");
        sink.OnThinkingComplete();

        Assert.Equal(2, events.Count);
        Assert.IsType<ThinkingDeltaEvent>(events[0]);
        Assert.IsType<ThinkingCompleteEvent>(events[1]);
    }

    [Fact]
    public void Existing_sink_behavior_unchanged_no_regression()
    {
        var events = new List<UiEvent>();
        var tp = new ManualTimeProvider();
        IAgentSink sink = new TuiAgentSink(new CollectingPublisher(events), tp);

        sink.OnAssistantText("hello");
        sink.OnAssistantTextComplete();
        sink.OnThinking("reasoning");
        sink.OnThinkingComplete();
        sink.OnError("boom");

        Assert.Equal(5, events.Count);
        Assert.IsType<AssistantTextDeltaEvent>(events[0]);
        Assert.IsType<AssistantTextCompletedEvent>(events[1]);
        Assert.IsType<ThinkingDeltaEvent>(events[2]);
        Assert.IsType<ThinkingCompleteEvent>(events[3]);
        Assert.IsType<AgentErrorEvent>(events[4]);
    }

    // -- Finding 2: ThinkingTokens flows through TuiAgentSink ---

    [Fact]
    public void OnThinkingComplete_with_token_count_carries_tokens_in_ThinkingCompleteEvent()
    {
        var events = new List<UiEvent>();
        var tp = new ManualTimeProvider();
        IAgentSink sink = new TuiAgentSink(new CollectingPublisher(events), tp);

        sink.OnThinking("reasoning");
        sink.OnThinkingComplete(thinkingTokens: 777);

        var complete = Assert.IsType<ThinkingCompleteEvent>(events[^1]);
        Assert.Equal(777, complete.ThinkingTokens);
    }

    [Fact]
    public void OnThinkingComplete_without_tokens_carries_null_in_ThinkingCompleteEvent()
    {
        var events = new List<UiEvent>();
        var tp = new ManualTimeProvider();
        IAgentSink sink = new TuiAgentSink(new CollectingPublisher(events), tp);

        sink.OnThinking("reasoning");
        sink.OnThinkingComplete();

        var complete = Assert.IsType<ThinkingCompleteEvent>(events[^1]);
        Assert.Null(complete.ThinkingTokens);
    }
}
