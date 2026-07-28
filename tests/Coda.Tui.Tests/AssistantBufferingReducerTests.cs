using System.Collections.Immutable;
using Coda.Agent;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;
using LlmClient;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for hooks Phase 3: static display-buffering decision in <see cref="UiReducer"/>.
///
/// <para>When <see cref="UiSessionSnapshot.BufferAssistantText"/> is false (the default, covering all
/// sessions without display-mutating AgentResponse hooks) the reducer is byte-identical to today:
/// assistant text deltas stream straight to the transcript and no buffering state is tracked.
/// When true, text deltas are accumulated in <see cref="UiSessionSnapshot.AssistantBuffer"/> and only
/// flushed to the transcript as a single completed block at turn completion or interruption.</para>
/// </summary>
public sealed class AssistantBufferingReducerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(5);
    private static readonly DateTimeOffset T2 = T0.AddSeconds(30);

    // =========================================================================
    // Test 1 — Without buffering (default), assistant deltas reach the transcript
    // =========================================================================

    [Fact]
    public void Without_buffering_assistant_deltas_reach_transcript_as_before()
    {
        var state = UiSessionSnapshot.Empty; // BufferAssistantText defaults to false

        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("hel"));
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("lo"));
        state = UiReducer.Reduce(state, new AssistantTextCompletedEvent());

        var block = Assert.IsType<AssistantTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("hello", block.Text);
        Assert.True(block.Complete);
        Assert.Null(state.AssistantBuffer); // no buffering state
    }

    [Fact]
    public void Without_buffering_TurnStarted_sets_active_operation_but_no_buffer()
    {
        var state = UiSessionSnapshot.Empty;

        state = UiReducer.Reduce(state, new TurnStartedEvent("hi", T0));

        Assert.NotNull(state.ActiveOperation);
        Assert.Equal("turn", state.ActiveOperation!.Kind);
        Assert.Null(state.AssistantBuffer);
        Assert.Null(state.BufferingStartedAt);
        Assert.Equal(0, state.BufferedOutputTokens);
    }

    // =========================================================================
    // Test 2 — With buffering on, assistant text deltas do NOT produce transcript
    //          content mid-turn; the placeholder/buffer state is active instead
    // =========================================================================

    [Fact]
    public void With_buffering_TurnStarted_activates_buffer_and_records_start_time()
    {
        var state = UiSessionSnapshot.Empty with { BufferAssistantText = true };

        state = UiReducer.Reduce(state, new TurnStartedEvent("hi", T0));

        Assert.Equal(string.Empty, state.AssistantBuffer);
        Assert.Equal(T0, state.BufferingStartedAt);
        Assert.Equal(0, state.BufferedOutputTokens);
    }

    [Fact]
    public void With_buffering_assistant_deltas_accumulate_in_buffer_not_transcript()
    {
        var state = BufferingStateAfterTurnStarted();

        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("hel"));
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("lo"));

        Assert.Equal("hello", state.AssistantBuffer);
        Assert.Empty(state.Transcript); // nothing in the transcript yet
    }

    [Fact]
    public void With_buffering_AssistantTextCompleted_does_not_flush_mid_turn()
    {
        var state = BufferingStateAfterTurnStarted();

        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("hello"));
        state = UiReducer.Reduce(state, new AssistantTextCompletedEvent());

        // Buffer still active; nothing in transcript
        Assert.Equal("hello", state.AssistantBuffer);
        Assert.Empty(state.Transcript);
    }

    // =========================================================================
    // Test 3 — With buffering on, tool activity still streams mid-turn
    // =========================================================================

    [Fact]
    public void With_buffering_tool_events_stream_to_transcript_unchanged()
    {
        var state = BufferingStateAfterTurnStarted();

        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("some text"));
        state = UiReducer.Reduce(state, new ToolStartedEvent("bash", "{}"));
        state = UiReducer.Reduce(state, new ToolCompletedEvent("bash", new ToolResult("ok", false)));

        // Tool block is in the transcript; assistant buffer is untouched
        var toolBlock = Assert.IsType<ToolTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("bash", toolBlock.ToolName);
        Assert.True(toolBlock.Complete);
        Assert.Equal("some text", state.AssistantBuffer); // still buffering
    }

    [Fact]
    public void With_buffering_tool_activity_events_stream_to_transcript()
    {
        var state = BufferingStateAfterTurnStarted();
        var identity = new ToolCallIdentity("turn1", "act1", "call1", "src1");

        state = UiReducer.Reduce(state, new ToolQueuedEvent(identity, "bash", "{}"));
        state = UiReducer.Reduce(state, new ToolStartedEvent("bash", "{}", identity));

        // Activity block in transcript
        Assert.Single(state.Transcript);
        Assert.IsType<ToolActivityTranscriptBlock>(state.Transcript[0]);
    }

    // =========================================================================
    // Test 4 — On turn completion the final text renders as one assistant block
    // =========================================================================

    [Fact]
    public void On_TurnCompleted_buffered_text_renders_as_completed_assistant_block()
    {
        var state = BufferingStateAfterTurnStarted();
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("hello "));
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("world"));
        state = UiReducer.Reduce(state, new TurnCompletedEvent(true));

        var block = Assert.IsType<AssistantTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("hello world", block.Text);
        Assert.True(block.Complete);

        // Buffer cleared
        Assert.Null(state.AssistantBuffer);
        Assert.Null(state.BufferingStartedAt);
        Assert.Equal(0, state.BufferedOutputTokens);
        Assert.Null(state.ActiveOperation);
    }

    [Fact]
    public void On_TurnCompleted_with_ResponseRewritten_the_display_content_is_used()
    {
        var state = BufferingStateAfterTurnStarted();
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("raw response"));
        state = UiReducer.Reduce(state, new ResponseRewrittenEvent("hook", "raw response", "clean response", null));
        state = UiReducer.Reduce(state, new TurnCompletedEvent(true));

        var block = Assert.IsType<AssistantTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("clean response", block.Text);
        Assert.True(block.Complete);
    }

    [Fact]
    public void On_TurnCompleted_empty_buffer_does_not_add_empty_block()
    {
        var state = BufferingStateAfterTurnStarted();
        // No text deltas; just complete the turn
        state = UiReducer.Reduce(state, new TurnCompletedEvent(true));

        Assert.Empty(state.Transcript);
        Assert.Null(state.AssistantBuffer);
    }

    [Fact]
    public void On_TurnCompleted_tool_blocks_are_preserved_alongside_final_assistant_block()
    {
        var state = BufferingStateAfterTurnStarted();
        state = UiReducer.Reduce(state, new ToolStartedEvent("bash", "{}"));
        state = UiReducer.Reduce(state, new ToolCompletedEvent("bash", new ToolResult("ok", false)));
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("final text"));
        state = UiReducer.Reduce(state, new TurnCompletedEvent(true));

        Assert.Equal(2, state.Transcript.Length);
        Assert.IsType<ToolTranscriptBlock>(state.Transcript[0]);
        var assistantBlock = Assert.IsType<AssistantTranscriptBlock>(state.Transcript[1]);
        Assert.Equal("final text", assistantBlock.Text);
    }

    // =========================================================================
    // Test 5 — Interruption mid-turn flushes buffered text (not discards)
    // =========================================================================

    [Fact]
    public void On_TurnInterrupted_buffered_text_is_flushed_not_discarded()
    {
        var state = BufferingStateAfterTurnStarted();
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("partial "));
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("response"));
        state = UiReducer.Reduce(state, new TurnInterruptedEvent());

        // Raw buffered text must NOT be shown — a marker is shown instead.
        var block = Assert.IsType<NoticeTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal(UiReducer.InterruptionMarker, block.Text);
        Assert.Equal(UiNotificationLevel.Warning, block.Level);

        // Buffer cleared
        Assert.Null(state.AssistantBuffer);
        Assert.Null(state.BufferingStartedAt);
    }

    [Fact]
    public void On_TurnInterrupted_empty_buffer_does_not_add_empty_block()
    {
        var state = BufferingStateAfterTurnStarted();
        state = UiReducer.Reduce(state, new TurnInterruptedEvent());

        Assert.Empty(state.Transcript);
        Assert.Null(state.AssistantBuffer);
    }

    // =========================================================================
    // Test 6 — Placeholder carries elapsed time and token count
    // =========================================================================

    [Fact]
    public void BufferingStartedAt_is_set_when_buffering_starts()
    {
        var state = UiSessionSnapshot.Empty with { BufferAssistantText = true };
        state = UiReducer.Reduce(state, new TurnStartedEvent("prompt", T0));

        Assert.Equal(T0, state.BufferingStartedAt);
    }

    [Fact]
    public void BufferedOutputTokens_accumulates_on_usage_events_during_buffering()
    {
        var state = BufferingStateAfterTurnStarted();

        state = UiReducer.Reduce(state, new UsageEvent(new TokenUsage(100, 50)));
        state = UiReducer.Reduce(state, new UsageEvent(new TokenUsage(0, 30)));

        Assert.Equal(80, state.BufferedOutputTokens); // 50 + 30
    }

    [Fact]
    public void SessionUsage_is_also_updated_during_buffered_turn()
    {
        var state = BufferingStateAfterTurnStarted();

        state = UiReducer.Reduce(state, new UsageEvent(new TokenUsage(100, 50)));

        Assert.Equal(100, state.SessionUsage.InputTokens);
        Assert.Equal(50, state.SessionUsage.OutputTokens);
    }

    [Fact]
    public void OperationalStatusProjector_shows_Writing_for_buffered_turn()
    {
        var state = BufferingStateAfterTurnStarted();

        var status = OperationalStatusProjector.Project(state);

        Assert.Equal(OperationalTone.Working, status.Tone);
        Assert.True(status.Animated);
        Assert.Contains("Writing", status.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OperationalStatusProjector_carries_StartedAt_for_elapsed_display()
    {
        var state = BufferingStateAfterTurnStarted();

        var status = OperationalStatusProjector.Project(state);

        Assert.Equal(T0, status.StartedAt);
    }

    [Fact]
    public void OperationalStatusProjector_includes_token_count_when_nonzero()
    {
        var state = BufferingStateAfterTurnStarted();
        state = UiReducer.Reduce(state, new UsageEvent(new TokenUsage(100, 1500)));

        var status = OperationalStatusProjector.Project(state);

        Assert.Contains("1.5k", status.Text, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Test 7 — Static decision: flag is never changed by events
    // =========================================================================

    [Fact]
    public void BufferAssistantText_is_preserved_across_all_events()
    {
        var state = UiSessionSnapshot.Empty with { BufferAssistantText = true };
        var allEvents = new UiEvent[]
        {
            new TurnStartedEvent("hi", T0),
            new AssistantTextDeltaEvent("delta"),
            new UsageEvent(new TokenUsage(10, 5)),
            new AssistantTextCompletedEvent(),
            new TurnCompletedEvent(true),
        };

        foreach (var evt in allEvents)
        {
            state = UiReducer.Reduce(state, evt);
        }

        // After a full turn cycle, the flag is still true
        Assert.True(state.BufferAssistantText);
    }

    [Fact]
    public void BufferAssistantText_false_is_preserved_across_all_events()
    {
        var state = UiSessionSnapshot.Empty; // false by default
        var allEvents = new UiEvent[]
        {
            new TurnStartedEvent("hi", T0),
            new AssistantTextDeltaEvent("delta"),
            new TurnCompletedEvent(true),
        };

        foreach (var evt in allEvents)
        {
            state = UiReducer.Reduce(state, evt);
        }

        Assert.False(state.BufferAssistantText);
    }

    [Fact]
    public void Snapshot_with_buffering_true_preserves_flag_in_with_expression()
    {
        // Verify that `state with { }` does not accidentally reset the flag.
        var state = UiSessionSnapshot.Empty with { BufferAssistantText = true };
        var mutated = state with { Mode = "something" };
        Assert.True(mutated.BufferAssistantText);
    }

    // =========================================================================
    // Thinking suppression during buffered turn
    // =========================================================================

    [Fact]
    public void With_buffering_thinking_deltas_are_suppressed_from_transcript()
    {
        var state = BufferingStateAfterTurnStarted();

        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("internal reasoning", T0));
        state = UiReducer.Reduce(state, new ThinkingCompleteEvent(1000L));

        // No thinking block added to transcript
        Assert.Empty(state.Transcript);
    }

    [Fact]
    public void Without_buffering_thinking_still_works_normally()
    {
        var state = UiSessionSnapshot.Empty; // buffering off

        state = UiReducer.Reduce(state, new ThinkingDeltaEvent("reasoning", T0));

        Assert.Single(state.Transcript);
        Assert.IsType<ThinkingTranscriptBlock>(state.Transcript[0]);
    }

    // =========================================================================
    // ResponseRewrittenEvent handling
    // =========================================================================

    [Fact]
    public void ResponseRewrittenEvent_updates_buffer_when_buffering_active()
    {
        var state = BufferingStateAfterTurnStarted();
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("raw text"));

        state = UiReducer.Reduce(state, new ResponseRewrittenEvent("hook", "raw text", "clean text", null));

        Assert.Equal("clean text", state.AssistantBuffer);
    }

    [Fact]
    public void ResponseRewrittenEvent_is_ignored_when_not_buffering()
    {
        var state = UiSessionSnapshot.Empty; // buffering off

        state = UiReducer.Reduce(state, new ResponseRewrittenEvent("hook", "raw", "clean", null));

        Assert.Empty(state.Transcript);
        Assert.Null(state.AssistantBuffer);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static UiSessionSnapshot BufferingStateAfterTurnStarted()
    {
        var state = UiSessionSnapshot.Empty with { BufferAssistantText = true };
        return UiReducer.Reduce(state, new TurnStartedEvent("test prompt", T0));
    }

    // =========================================================================
    // I1 — interruption / error withholds raw buffer unless hook already ran
    // =========================================================================

    [Fact]
    public void On_TurnInterrupted_after_ResponseRewritten_flushes_hook_content_not_marker()
    {
        // Hook ran and rewrote the buffer before interruption.
        var state = BufferingStateAfterTurnStarted();
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("raw secret"));
        state = UiReducer.Reduce(state, new ResponseRewrittenEvent("hook", "raw secret", "REDACTED", null));
        state = UiReducer.Reduce(state, new TurnInterruptedEvent());

        // The hook's display content (not raw text, not marker) is shown.
        var block = Assert.IsType<AssistantTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("REDACTED", block.Text);
        Assert.True(block.Complete);

        Assert.Null(state.AssistantBuffer);
        Assert.False(state.BufferRewrittenByHook);
    }

    [Fact]
    public void On_TurnInterrupted_empty_ResponseRewritten_suppresses_output_fully()
    {
        // Hook ran and set displayContent: "" (full suppression) before interruption.
        var state = BufferingStateAfterTurnStarted();
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("raw secret"));
        state = UiReducer.Reduce(state, new ResponseRewrittenEvent("hook", "raw secret", string.Empty, null));
        state = UiReducer.Reduce(state, new TurnInterruptedEvent());

        // Empty hook content → no transcript block at all.
        Assert.Empty(state.Transcript);
        Assert.Null(state.AssistantBuffer);
    }

    [Fact]
    public void On_TurnCompleted_error_without_ResponseRewritten_shows_marker_not_raw_text()
    {
        var state = BufferingStateAfterTurnStarted();
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("sensitive data"));
        state = UiReducer.Reduce(state, new TurnCompletedEvent(false));

        // Marker shown instead of raw text.
        var block = Assert.IsType<NoticeTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal(UiReducer.InterruptionMarker, block.Text);
        Assert.Equal(UiNotificationLevel.Warning, block.Level);

        Assert.Null(state.AssistantBuffer);
    }

    [Fact]
    public void BufferRewrittenByHook_reset_to_false_at_turn_start()
    {
        // Simulate a rewrite in one turn, then verify the flag resets for the next turn.
        var state = BufferingStateAfterTurnStarted();
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("text"));
        state = UiReducer.Reduce(state, new ResponseRewrittenEvent("cmd", "text", "CLEAN", null));
        state = UiReducer.Reduce(state, new TurnCompletedEvent(true));

        // Start next turn.
        state = UiReducer.Reduce(state, new TurnStartedEvent("next", T1));
        Assert.False(state.BufferRewrittenByHook);
    }
}
