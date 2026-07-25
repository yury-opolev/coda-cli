using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for the ThinkingTranscriptBlock lifecycle in <see cref="UiReducer"/>: burst creation on the
/// first delta, delta accumulation, completion with frozen ElapsedMs, multiple interleaved bursts,
/// and auto-finalization on turn completion.
/// </summary>
public sealed class ThinkingReducerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_ThinkingDelta_creates_a_new_incomplete_block()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("hello", T0));

        var block = Assert.IsType<ThinkingTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("hello", block.Text);
        Assert.False(block.Complete);
        Assert.Equal(T0, block.StartedAt);
        Assert.Null(block.ElapsedMs);
        Assert.Null(block.ThinkingTokens);
    }

    [Fact]
    public void Subsequent_deltas_append_to_existing_incomplete_block()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("hello", T0));
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent(" world", T0));

        var block = Assert.IsType<ThinkingTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("hello world", block.Text);
    }

    [Fact]
    public void Delta_accumulation_preserves_block_id()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("a", T0));
        var id = state.Transcript[0].Id;
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("b", T0));

        Assert.Equal(id, state.Transcript[0].Id);
    }

    [Fact]
    public void ThinkingComplete_marks_block_complete_and_freezes_elapsedMs()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("reasoning", T0));
        state = UiReducer.Reduce(state, new ThinkingCompleteEvent(1234L));

        var block = Assert.IsType<ThinkingTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.True(block.Complete);
        Assert.Equal(1234L, block.ElapsedMs);
    }

    [Fact]
    public void ThinkingComplete_without_prior_delta_is_a_no_op()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new ThinkingCompleteEvent(500L));

        Assert.Empty(state.Transcript);
    }

    [Fact]
    public void Multiple_bursts_produce_multiple_independent_blocks()
    {
        var state = UiSessionSnapshot.Empty;

        // First burst
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("burst 1", T0));
        state = UiReducer.Reduce(state, new ThinkingCompleteEvent(100L));

        // Second burst
        var t1 = T0.AddSeconds(1);
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("burst 2", t1));
        state = UiReducer.Reduce(state, new ThinkingCompleteEvent(200L));

        Assert.Equal(2, state.Transcript.Length);

        var first = Assert.IsType<ThinkingTranscriptBlock>(state.Transcript[0]);
        Assert.Equal("burst 1", first.Text);
        Assert.True(first.Complete);
        Assert.Equal(100L, first.ElapsedMs);

        var second = Assert.IsType<ThinkingTranscriptBlock>(state.Transcript[1]);
        Assert.Equal("burst 2", second.Text);
        Assert.True(second.Complete);
        Assert.Equal(200L, second.ElapsedMs);
    }

    [Fact]
    public void Thinking_blocks_interleave_with_assistant_and_tool_blocks_in_order()
    {
        var state = UiSessionSnapshot.Empty;

        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("think", T0));
        state = UiReducer.Reduce(state, new ThinkingCompleteEvent(50L));
        state = UiReducer.Reduce(state, new ToolStartedEvent("bash", "{}"));
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("answer"));
        state = UiReducer.Reduce(state, new AssistantTextCompletedEvent());

        Assert.Equal(3, state.Transcript.Length);
        Assert.IsType<ThinkingTranscriptBlock>(state.Transcript[0]);
        Assert.IsType<ToolTranscriptBlock>(state.Transcript[1]);
        Assert.IsType<AssistantTranscriptBlock>(state.Transcript[2]);
    }

    [Fact]
    public void Second_burst_delta_does_not_append_to_completed_first_burst()
    {
        var state = UiSessionSnapshot.Empty;

        // First burst completed
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("first", T0));
        state = UiReducer.Reduce(state, new ThinkingCompleteEvent(100L));

        // Second burst starts
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("second", T0.AddSeconds(1)));

        Assert.Equal(2, state.Transcript.Length);
        var second = Assert.IsType<ThinkingTranscriptBlock>(state.Transcript[1]);
        Assert.Equal("second", second.Text);
        Assert.False(second.Complete);
    }

    // ─── Fix 1: turn-level finalization ──────────────────────────────────────────

    [Fact]
    public void TurnCompleted_success_finalizes_open_thinking_block()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("still thinking", T0));
        state = UiReducer.Reduce(state, new TurnCompletedEvent(Success: true));

        var block = Assert.IsType<ThinkingTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.True(block.Complete);
        Assert.NotNull(block.ElapsedMs);
    }

    [Fact]
    public void TurnCompleted_failure_finalizes_open_thinking_block()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("still thinking", T0));
        state = UiReducer.Reduce(state, new TurnCompletedEvent(Success: false));

        var block = Assert.IsType<ThinkingTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.True(block.Complete);
        Assert.NotNull(block.ElapsedMs);
    }

    [Fact]
    public void TurnInterrupted_finalizes_open_thinking_block()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("still thinking", T0));
        state = UiReducer.Reduce(state, new TurnInterruptedEvent());

        var block = Assert.IsType<ThinkingTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.True(block.Complete);
        Assert.NotNull(block.ElapsedMs);
    }

    [Fact]
    public void TurnCompleted_with_already_complete_block_leaves_ElapsedMs_unchanged()
    {
        // Completed normally via ThinkingCompleteEvent — the frozen value must not be overwritten.
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("thinking", T0));
        state = UiReducer.Reduce(state, new ThinkingCompleteEvent(1000L));
        state = UiReducer.Reduce(state, new TurnCompletedEvent(Success: true));

        var block = Assert.IsType<ThinkingTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal(1000L, block.ElapsedMs);
    }

    // -- Finding 2: ThinkingTokens carried on block ---

    [Fact]
    public void ThinkingCompleteEvent_with_ThinkingTokens_sets_tokens_on_block()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("reasoning", T0));
        state = UiReducer.Reduce(state, new ThinkingCompleteEvent(500L, ThinkingTokens: 123));

        var block = Assert.IsType<ThinkingTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.True(block.Complete);
        Assert.Equal(500L, block.ElapsedMs);
        Assert.Equal(123, block.ThinkingTokens);
    }

    [Fact]
    public void ThinkingCompleteEvent_without_ThinkingTokens_leaves_tokens_null_on_block()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("reasoning", T0));
        state = UiReducer.Reduce(state, new ThinkingCompleteEvent(500L));

        var block = Assert.IsType<ThinkingTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Null(block.ThinkingTokens);
    }
}
