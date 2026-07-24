using Coda.Agent;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class ToolActivityReducerTests
{
    [Fact]
    public void Later_queued_batches_use_one_stable_activity_block_in_provider_order()
    {
        var first = Identity("root", "activity", "call-1", "root");
        var second = Identity("root", "activity", "call-2", "subagent:lint");
        var third = Identity("root", "activity", "call-3", "root");

        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, new ToolQueuedEvent(first, "read", "{\"path\":\"a\"}"));
        var initial = Activity(state);

        state = UiReducer.Reduce(state, new ToolQueuedEvent(second, "grep", "{\"query\":\"b\"}"));
        state = UiReducer.Reduce(state, new ToolQueuedEvent(third, "write", "{\"path\":\"c\"}"));

        var activity = Activity(state);
        Assert.Equal(initial.Id, activity.Id);
        Assert.Equal(["call-1", "call-2", "call-3"], activity.Calls.Select(call => call.CallId));
        Assert.Equal(["root", "subagent:lint", "root"], activity.Calls.Select(call => call.SourceId));
    }

    [Fact]
    public void Same_call_id_from_different_sources_and_same_tool_names_stay_distinct()
    {
        var fromRoot = Identity("root", "activity", "call", "root");
        var fromSubagent = Identity("root", "activity", "call", "subagent:review");

        var state = Reduce(
            new ToolQueuedEvent(fromRoot, "read", "{\"path\":\"root.txt\"}"),
            new ToolQueuedEvent(fromSubagent, "read", "{\"path\":\"review.txt\"}"));

        var activity = Activity(state);
        Assert.Equal(2, activity.Calls.Length);
        Assert.Equal(["root", "subagent:review"], activity.Calls.Select(call => call.SourceId));
        Assert.All(activity.Calls, call => Assert.Equal("read", call.ToolName));
    }

    [Fact]
    public void Duplicate_queue_updates_one_existing_call_without_changing_block_id_or_order()
    {
        var identity = Identity("root", "activity", "call", "root");
        var updatedInput = "{\"input\":\"one\\ntwo\\u001b[2J" + new string('x', 200) + "\"}";
        var state = UiReducer.Reduce(
            UiSessionSnapshot.Empty,
            new ToolQueuedEvent(identity, "old_name", "{\"input\":\"old\"}"));
        var initialId = Activity(state).Id;

        state = UiReducer.Reduce(
            state,
            new ToolQueuedEvent(identity, "new_name", updatedInput));

        var activity = Activity(state);
        var call = Assert.Single(activity.Calls);
        Assert.Equal(initialId, activity.Id);
        Assert.Equal("new_name", call.ToolName);
        Assert.Equal(updatedInput, call.InputJson);
        Assert.DoesNotContain('\n', call.SafePreview);
        Assert.DoesNotContain('\r', call.SafePreview);
        Assert.DoesNotContain('\u001b', call.SafePreview);
        Assert.Equal(128, call.SafePreview.Length);
        Assert.False(call.IsOrphan);
    }

    [Fact]
    public void Correlated_started_status_and_progress_target_only_the_exact_call_key()
    {
        var first = Identity("root", "activity", "call", "root");
        var second = Identity("root", "activity", "call", "subagent:review");
        var state = Reduce(
            new ToolQueuedEvent(first, "read", "{}"),
            new ToolQueuedEvent(second, "read", "{}"),
            new ToolStateChangedEvent(first, "read", ToolCallStatus.AwaitingApproval),
            new ToolStartedEvent("read", "{}", second),
            new ToolProgressEvent("read", 42, second));

        var activity = Activity(state);
        Assert.Equal(ToolCallStatus.AwaitingApproval, activity.Calls[0].Status);
        Assert.Null(activity.Calls[0].ElapsedMs);
        Assert.Equal(ToolCallStatus.Running, activity.Calls[1].Status);
        Assert.Equal(42, activity.Calls[1].ElapsedMs);
    }

    [Fact]
    public void Unknown_correlated_status_and_progress_create_one_explicit_orphan()
    {
        var known = Identity("root", "activity", "known", "root");
        var unknown = Identity("root", "activity", "unknown", "root");
        var state = Reduce(
            new ToolQueuedEvent(known, "read", "{}"),
            new ToolStateChangedEvent(unknown, "read", ToolCallStatus.AwaitingApproval),
            new ToolProgressEvent("read", 42, unknown));

        var activity = Activity(state);
        Assert.Equal(2, activity.Calls.Length);
        Assert.Equal(ToolCallStatus.Pending, activity.Calls[0].Status);

        var orphan = activity.Calls[1];
        Assert.Equal("unknown", orphan.CallId);
        Assert.True(orphan.IsOrphan);
        Assert.Equal(ToolCallStatus.AwaitingApproval, orphan.Status);
        Assert.Equal(42, orphan.ElapsedMs);
        Assert.Equal(string.Empty, orphan.InputJson);
        Assert.Equal(string.Empty, orphan.SafePreview);
    }

    [Fact]
    public void Unknown_completion_appends_an_orphan_without_changing_a_known_same_name_call()
    {
        var known = Identity("root", "activity", "known", "root");
        var unknown = Identity("root", "activity", "unknown", "root");
        var state = Reduce(
            new ToolQueuedEvent(known, "read", "{\"path\":\"known\"}"),
            new ToolCompletedEvent("read", new ToolResult("failure", IsError: true), unknown, ToolCallStatus.Failed));

        var activity = Activity(state);
        Assert.Equal(2, activity.Calls.Length);

        var existing = activity.Calls[0];
        Assert.Equal("known", existing.CallId);
        Assert.Equal(ToolCallStatus.Pending, existing.Status);
        Assert.Null(existing.Result);
        Assert.Null(existing.Error);

        var orphan = activity.Calls[1];
        Assert.Equal("unknown", orphan.CallId);
        Assert.True(orphan.IsOrphan);
        Assert.Equal(ToolCallStatus.Failed, orphan.Status);
        Assert.Null(orphan.Result);
        Assert.Equal("failure", orphan.Error);
    }

    [Fact]
    public void Completion_before_queue_creates_an_orphan_that_the_queue_resolves_without_duplication()
    {
        var identity = Identity("root", "activity", "call", "root");
        var state = Reduce(
            new ToolCompletedEvent("read", new ToolResult("failure", IsError: true), identity, ToolCallStatus.Failed));
        var orphanActivity = Activity(state);
        var orphanId = orphanActivity.Id;

        state = UiReducer.Reduce(state, new ToolQueuedEvent(identity, "read", "{\"path\":\"file.txt\"}"));

        var activity = Activity(state);
        var call = Assert.Single(activity.Calls);
        Assert.Equal(orphanId, activity.Id);
        Assert.False(call.IsOrphan);
        Assert.Equal("{\"path\":\"file.txt\"}", call.InputJson);
        Assert.Equal(ToolCallStatus.Failed, call.Status);
        Assert.Null(call.Result);
        Assert.Equal("failure", call.Error);
    }

    [Fact]
    public void Correlated_completion_uses_terminal_status_and_result_error_fields()
    {
        var succeeded = Identity("root", "activity", "succeeded", "root");
        var failed = Identity("root", "activity", "failed", "root");
        var state = Reduce(
            new ToolQueuedEvent(succeeded, "read", "{}"),
            new ToolQueuedEvent(failed, "write", "{}"),
            new ToolCompletedEvent("read", new ToolResult("ok", IsError: false), succeeded),
            new ToolCompletedEvent("write", new ToolResult("denied", IsError: true), failed, ToolCallStatus.Failed));

        var activity = Activity(state);
        Assert.Equal(ToolCallStatus.Succeeded, activity.Calls[0].Status);
        Assert.Equal("ok", activity.Calls[0].Result);
        Assert.Null(activity.Calls[0].Error);

        Assert.Equal(ToolCallStatus.Failed, activity.Calls[1].Status);
        Assert.Null(activity.Calls[1].Result);
        Assert.Equal("denied", activity.Calls[1].Error);
    }

    [Fact]
    public void Finalization_skips_pending_cancels_active_and_marks_cancelled_activity()
    {
        var pending = Identity("root", "activity", "pending", "root");
        var approval = Identity("root", "activity", "approval", "root");
        var running = Identity("root", "activity", "running", "root");
        var complete = Identity("root", "activity", "complete", "root");
        var state = Reduce(
            new ToolQueuedEvent(pending, "read", "{}"),
            new ToolQueuedEvent(approval, "read", "{}"),
            new ToolQueuedEvent(running, "read", "{}"),
            new ToolQueuedEvent(complete, "read", "{}"),
            new ToolStateChangedEvent(approval, "read", ToolCallStatus.AwaitingApproval),
            new ToolStartedEvent("read", "{}", running),
            new ToolCompletedEvent("read", new ToolResult("ok"), complete),
            new ToolActivityCompletedEvent(Summary("root", "activity", totalCalls: 4, cancelledCalls: 2, skippedCalls: 1)));

        var activity = Activity(state);
        Assert.Equal(
            [ToolCallStatus.Skipped, ToolCallStatus.Cancelled, ToolCallStatus.Cancelled, ToolCallStatus.Succeeded],
            activity.Calls.Select(call => call.Status));
        Assert.Equal(ToolActivityCompletionState.Cancelled, activity.CompletionState);
    }

    [Fact]
    public void Finalization_without_cancellation_is_completed_and_repeated_finalization_is_stable()
    {
        var identity = Identity("root", "activity", "pending", "root");
        var summary = Summary("root", "activity", totalCalls: 1, skippedCalls: 1);
        var state = Reduce(
            new ToolQueuedEvent(identity, "read", "{}"),
            new ToolActivityCompletedEvent(summary));
        var completed = Activity(state);

        var repeated = UiReducer.Reduce(state, new ToolActivityCompletedEvent(summary));
        var repeatedActivity = Activity(repeated);

        Assert.Equal(ToolCallStatus.Skipped, completed.Calls[0].Status);
        Assert.Equal(ToolActivityCompletionState.Completed, completed.CompletionState);
        Assert.Equal(completed.Id, repeatedActivity.Id);
        Assert.Equal(completed, repeatedActivity);
    }

    [Fact]
    public void Correlated_events_after_finalization_do_not_regress_the_terminal_call()
    {
        var identity = Identity("root", "activity", "call", "root");
        var state = Reduce(
            new ToolQueuedEvent(identity, "read", "{}"),
            new ToolCompletedEvent("read", new ToolResult("ok"), identity),
            new ToolActivityCompletedEvent(Summary("root", "activity", totalCalls: 1)));
        var finalized = Activity(state);

        state = Reduce(
            state,
            new ToolStateChangedEvent(identity, "read", ToolCallStatus.Running),
            new ToolProgressEvent("read", 99, identity),
            new ToolCompletedEvent("read", new ToolResult("late failure", IsError: true), identity, ToolCallStatus.Failed));

        var activity = Activity(state);
        var call = Assert.Single(activity.Calls);
        Assert.Equal(finalized.Id, activity.Id);
        Assert.Equal(ToolActivityCompletionState.Completed, activity.CompletionState);
        Assert.Equal(ToolCallStatus.Succeeded, call.Status);
        Assert.Equal("ok", call.Result);
        Assert.Null(call.Error);
        Assert.Null(call.ElapsedMs);
    }

    [Fact]
    public void Mismatched_root_or_activity_events_and_summaries_do_not_corrupt_another_activity()
    {
        var known = Identity("root", "activity", "known", "root");
        var wrongRoot = Identity("other-root", "activity", "known", "root");
        var wrongActivity = Identity("root", "other-activity", "known", "root");
        var state = Reduce(
            new ToolQueuedEvent(known, "read", "{}"),
            new ToolStateChangedEvent(wrongRoot, "read", ToolCallStatus.Running),
            new ToolCompletedEvent("read", new ToolResult("wrong", IsError: true), wrongActivity, ToolCallStatus.Failed),
            new ToolActivityCompletedEvent(Summary("missing-root", "missing-activity", totalCalls: 1)));

        var activity = Assert.Single(
            state.Transcript.OfType<ToolActivityTranscriptBlock>(),
            block => block.RootTurnId == "root" && block.ActivityId == "activity");
        var call = Assert.Single(activity.Calls);

        Assert.Equal("known", call.CallId);
        Assert.Equal(ToolCallStatus.Pending, call.Status);
        Assert.Null(call.Result);
        Assert.Null(call.Error);
        Assert.Equal(ToolActivityCompletionState.Active, activity.CompletionState);
    }

    [Fact]
    public void Legacy_identity_null_events_keep_the_original_tool_transcript_block_behavior()
    {
        var state = Reduce(
            new ToolStartedEvent("bash", "{}"),
            new ToolProgressEvent("bash", 1500),
            new ToolCompletedEvent("bash", new ToolResult("out", IsError: true)));

        var tool = Assert.IsType<ToolTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("bash", tool.ToolName);
        Assert.Equal(1500, tool.ElapsedMs);
        Assert.Equal("out", tool.Result);
        Assert.True(tool.IsError);
        Assert.True(tool.Complete);
        Assert.DoesNotContain(state.Transcript, block => block is ToolActivityTranscriptBlock);
    }

    [Fact]
    public void Activity_inserts_one_visible_block_and_later_replacements_keep_its_anchor_id()
    {
        var identity = Identity("root", "activity", "call", "root");
        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, new UserPromptSubmittedEvent("prompt"));

        state = UiReducer.Reduce(state, new ToolQueuedEvent(identity, "read", "{}"));
        var activity = Activity(state);
        var id = activity.Id;
        Assert.Equal(2, state.Transcript.Length);

        state = Reduce(
            state,
            new ToolStartedEvent("read", "{}", identity),
            new ToolStateChangedEvent(identity, "read", ToolCallStatus.Running),
            new ToolProgressEvent("read", 5, identity),
            new ToolCompletedEvent("read", new ToolResult("ok"), identity),
            new ToolActivityCompletedEvent(Summary("root", "activity", totalCalls: 1)));

        var final = Assert.IsType<ToolActivityTranscriptBlock>(state.Transcript[1]);
        Assert.Equal(2, state.Transcript.Length);
        Assert.Equal(id, final.Id);
        Assert.Equal(ToolActivityCompletionState.Completed, final.CompletionState);
    }

    private static ToolCallIdentity Identity(string rootTurnId, string activityId, string callId, string sourceId) =>
        new(rootTurnId, activityId, callId, sourceId);

    private static ToolActivitySummary Summary(
        string rootTurnId,
        string activityId,
        int totalCalls,
        int failedCalls = 0,
        int cancelledCalls = 0,
        int skippedCalls = 0) =>
        new(rootTurnId, activityId, totalCalls, failedCalls, cancelledCalls, skippedCalls, null);

    private static UiSessionSnapshot Reduce(params UiEvent[] events) =>
        Reduce(UiSessionSnapshot.Empty, events);

    private static UiSessionSnapshot Reduce(UiSessionSnapshot state, params UiEvent[] events)
    {
        foreach (var uiEvent in events)
        {
            state = UiReducer.Reduce(state, uiEvent);
        }

        return state;
    }

    private static ToolActivityTranscriptBlock Activity(UiSessionSnapshot state) =>
        Assert.Single(state.Transcript.OfType<ToolActivityTranscriptBlock>());
}
