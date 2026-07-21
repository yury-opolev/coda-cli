using System.Collections.Immutable;
using System.Reflection;
using Coda.Agent;
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Lsp;
using Coda.Mcp;
using Coda.Sdk;
using Coda.Sdk.Scheduling;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class UiReducerTests
{
    [Fact]
    public void Assistant_deltas_merge_into_one_block_and_complete()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("hel"));
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("lo"));

        var block = Assert.IsType<AssistantTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("hello", block.Text);
        Assert.False(block.Complete);

        state = UiReducer.Reduce(state, new AssistantTextCompletedEvent());
        var completed = Assert.IsType<AssistantTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.True(completed.Complete);
    }

    [Fact]
    public void Assistant_delta_replacement_preserves_id()
    {
        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, new AssistantTextDeltaEvent("a"));
        var id = state.Transcript[0].Id;

        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("b"));

        Assert.Equal(id, state.Transcript[0].Id);
    }

    [Fact]
    public void Tool_progress_replaces_active_block_preserving_id()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new ToolStartedEvent("bash", "{}"));
        var startedId = Assert.IsType<ToolTranscriptBlock>(Assert.Single(state.Transcript)).Id;

        state = UiReducer.Reduce(state, new ToolProgressEvent("bash", 1500));
        state = UiReducer.Reduce(state, new ToolProgressEvent("bash", 2400));

        var block = Assert.IsType<ToolTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal(startedId, block.Id);
        Assert.Equal(2400, block.ElapsedMs);
        Assert.False(block.Complete);
    }

    [Fact]
    public void Tool_completion_stores_result_and_marks_complete()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new ToolStartedEvent("bash", "{}"));
        state = UiReducer.Reduce(state, new ToolCompletedEvent("bash", new ToolResult("out", IsError: true)));

        var block = Assert.IsType<ToolTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("out", block.Result);
        Assert.True(block.IsError);
        Assert.True(block.Complete);
    }

    [Fact]
    public void Usage_stop_limit_and_error_preserve_semantics()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new UsageEvent(new TokenUsage(100, 20)));
        state = UiReducer.Reduce(state, new StopReasonEvent("max_tokens"));
        state = UiReducer.Reduce(state, new LimitReachedEvent("tokens", "limit hit"));
        state = UiReducer.Reduce(state, new AgentErrorEvent("boom"));

        Assert.Equal(100, state.SessionUsage.InputTokens);
        Assert.Equal(20, state.SessionUsage.OutputTokens);
        Assert.Equal("max_tokens", state.StopReason);
        Assert.NotNull(state.Notification);
        Assert.Equal("boom", state.Notification!.Message);
        Assert.Equal(UiNotificationLevel.Error, state.Notification.Level);
    }

    [Fact]
    public void Snapshot_public_property_types_are_not_terminal_gui()
    {
        foreach (var prop in typeof(UiSessionSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            AssertNotTerminalGui(prop.PropertyType, prop.Name);
            foreach (var arg in prop.PropertyType.GetGenericArguments())
            {
                AssertNotTerminalGui(arg, prop.Name);
            }
        }
    }

    private static void AssertNotTerminalGui(Type type, string owner)
    {
        var ns = type.Namespace ?? string.Empty;
        Assert.False(
            ns.StartsWith("Terminal.Gui", StringComparison.Ordinal),
            $"{owner} exposes Terminal.Gui type {type.FullName}");
    }

    [Fact]
    public void Projector_maps_user_and_assistant_history_to_completed_blocks()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.UserText("question"),
            new ChatMessage(ChatRole.Assistant, [new TextBlock("answer")]),
        };

        var blocks = SessionHistoryProjector.Project(history);

        Assert.Equal(2, blocks.Length);
        var user = Assert.IsType<UserTranscriptBlock>(blocks[0]);
        Assert.Equal("question", user.Text);
        var assistant = Assert.IsType<AssistantTranscriptBlock>(blocks[1]);
        Assert.Equal("answer", assistant.Text);
        Assert.True(assistant.Complete);
    }

    [Fact]
    public void Projector_omits_the_sent_time_for_resumed_history_without_a_timestamp()
    {
        var history = new List<ChatMessage> { ChatMessage.UserText("question") };

        var blocks = SessionHistoryProjector.Project(history);

        // The persisted ChatMessage model has no timestamp, so resumed user blocks carry a stable null
        // SentAt (the renderer omits the time) rather than inventing a changing draw-time value.
        Assert.Null(Assert.IsType<UserTranscriptBlock>(blocks[0]).SentAt);
    }

    [Fact]
    public void User_prompt_event_carries_its_sent_time_onto_the_transcript_block()
    {
        var sentAt = new DateTimeOffset(2026, 7, 21, 8, 24, 0, TimeSpan.FromHours(2));

        var state = UiReducer.Reduce(
            UiSessionSnapshot.Empty,
            new UserPromptSubmittedEvent("hi", sentAt));

        var block = Assert.IsType<UserTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("hi", block.Text);
        Assert.Equal(sentAt, block.SentAt);
    }

    [Fact]
    public void Projector_does_not_mutate_history()
    {
        var history = new List<ChatMessage> { ChatMessage.UserText("q") };
        var snapshot = history.ToList();

        SessionHistoryProjector.Project(history);

        Assert.Equal(snapshot.Count, history.Count);
        Assert.Same(snapshot[0], history[0]);
    }

    [Fact]
    public void Seed_replaces_transcript_without_mutating_input()
    {
        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, new UserPromptSubmittedEvent("first"));
        var before = state.Transcript;
        ImmutableArray<TranscriptBlock> seed = [new UserTranscriptBlock(Guid.NewGuid(), "seeded")];

        var seeded = UiReducer.Reduce(state, new TranscriptSeededEvent(seed));

        Assert.Equal("seeded", Assert.IsType<UserTranscriptBlock>(Assert.Single(seeded.Transcript)).Text);
        Assert.Equal("first", Assert.IsType<UserTranscriptBlock>(Assert.Single(before)).Text);
    }

    [Fact]
    public void Permission_request_and_resolve_track_pending_count()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new PermissionRequestedEvent("write", "file.txt"));

        Assert.Equal(1, state.Permission.PendingCount);
        Assert.Null(Assert.IsType<PermissionTranscriptBlock>(state.Transcript[0]).Allowed);

        state = UiReducer.Reduce(state, new PermissionResolvedEvent("write", true));

        Assert.Equal(0, state.Permission.PendingCount);
        Assert.True(Assert.IsType<PermissionTranscriptBlock>(state.Transcript[0]).Allowed);

        state = UiReducer.Reduce(state, new PermissionResolvedEvent("write", false));
        Assert.Equal(0, state.Permission.PendingCount);
    }

    [Fact]
    public void Console_clear_empties_and_transcript_cleared_adds_boundary()
    {
        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, new UserPromptSubmittedEvent("hi"));

        state = UiReducer.Reduce(state, new ConsoleClearRequestedEvent());
        Assert.Empty(state.Transcript);

        state = UiReducer.Reduce(state, new TranscriptClearedEvent("session-2"));
        var boundary = Assert.IsType<SessionBoundaryTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("session-2", boundary.SessionId);
        Assert.Equal("session-2", state.SessionId);
    }

    [Fact]
    public void Session_metadata_changed_updates_fields_and_preserves_pending()
    {
        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, new PermissionRequestedEvent("x", "y"));

        state = UiReducer.Reduce(state, new SessionMetadataChangedEvent(
            "sid", "prov", "model", "high", "high", "C:\\work", PermissionMode.AcceptEdits, true));

        Assert.Equal("sid", state.SessionId);
        Assert.Equal("prov", state.Provider);
        Assert.Equal("model", state.Model);
        Assert.Equal("high", state.RequestedEffort);
        Assert.Equal("high", state.EffectiveEffort);
        Assert.Equal("C:\\work", state.WorkingDirectory);
        Assert.Equal(PermissionMode.AcceptEdits, state.Permission.Mode);
        Assert.Equal(1, state.Permission.PendingCount);
        Assert.True(state.Connected);
    }

    [Fact]
    public void Runtime_snapshot_derives_running_tasks_and_lsp_counts()
    {
        var runtime = new SessionRuntimeSnapshot(
            "s1", TokenUsage.Zero, null, [], [],
            [
                new BackgroundTaskSnapshot("t1", BackgroundTaskStatus.Running),
                new BackgroundTaskSnapshot("t2", BackgroundTaskStatus.Completed),
            ],
            [
                new LspServerSnapshot("csharp", LspServerState.Running, []),
                new LspServerSnapshot("go", LspServerState.Error, []),
            ],
            []);

        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, new SessionRuntimeChangedEvent(runtime));

        Assert.Equal(1, state.RunningTasks);
        Assert.Equal(1, state.Lsp.Connected);
        Assert.Equal(1, state.Lsp.Error);
        Assert.Same(runtime, state.Runtime);
    }

    [Fact]
    public void Mcp_snapshot_derives_connected_and_error_counts()
    {
        var mcp = new McpRuntimeSnapshot(1,
        [
            new McpServerRuntimeSnapshot("a", new McpServerInfo("a", "1", null), 3),
            new McpServerRuntimeSnapshot("b", null, 0),
        ]);

        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, new McpRuntimeChangedEvent(mcp));

        Assert.Equal(1, state.Mcp.Connected);
        Assert.Equal(1, state.Mcp.Error);
        Assert.Same(mcp, state.McpRuntime);
    }

    [Fact]
    public void Turn_started_sets_operation_and_interrupt_clears_with_notification()
    {
        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, new TurnStartedEvent("do it"));
        Assert.NotNull(state.ActiveOperation);

        state = UiReducer.Reduce(state, new TurnInterruptedEvent());

        Assert.Null(state.ActiveOperation);
        Assert.NotNull(state.Notification);
        Assert.Equal(UiNotificationLevel.Warning, state.Notification!.Level);
    }

    [Fact]
    public void Active_operation_changed_sets_and_clears_the_operation()
    {
        var state = UiReducer.Reduce(
            UiSessionSnapshot.Empty,
            new ActiveOperationChangedEvent(new ActiveOperation("startup", "Starting…", null)));

        Assert.NotNull(state.ActiveOperation);
        Assert.Equal("startup", state.ActiveOperation!.Kind);
        Assert.Equal("Starting…", state.ActiveOperation.Label);

        state = UiReducer.Reduce(state, new ActiveOperationChangedEvent(null));

        Assert.Null(state.ActiveOperation);
    }

    [Fact]
    public void Null_publisher_is_a_no_op_singleton()
    {
        Assert.Same(NullUiEventPublisher.Instance, NullUiEventPublisher.Instance);
        NullUiEventPublisher.Instance.Publish(new AssistantTextCompletedEvent());
    }

    // ── Scheduled task lifecycle notices (Task 8) ─────────────────────────────

    [Fact]
    public void Schedule_started_notice_uses_information_and_task_id()
    {
        var (text, level) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", "Nightly", "task-9", ScheduleLifecycleKind.Started, DateTimeOffset.UnixEpoch, null));

        Assert.Equal("Scheduled task Nightly started as task-9", text);
        Assert.Equal(UiNotificationLevel.Information, level);
    }

    [Fact]
    public void Schedule_started_notice_falls_back_to_the_definition_id_when_unnamed()
    {
        var (text, _) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", null, "task-9", ScheduleLifecycleKind.Started, DateTimeOffset.UnixEpoch, null));

        Assert.Equal("Scheduled task def-1 started as task-9", text);
    }

    [Fact]
    public void Schedule_completed_notice_uses_information()
    {
        var (text, level) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", "Nightly", "task-9", ScheduleLifecycleKind.Completed, DateTimeOffset.UnixEpoch, "big result body"));

        // Concise text only — the full task result/output is never inserted.
        Assert.Equal("Scheduled task Nightly completed", text);
        Assert.Equal(UiNotificationLevel.Information, level);
    }

    [Fact]
    public void Schedule_failed_notice_uses_error_and_appends_a_short_summary()
    {
        var (text, level) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", "Nightly", "task-9", ScheduleLifecycleKind.Failed, DateTimeOffset.UnixEpoch, "boom happened"));

        Assert.Equal("Scheduled task Nightly failed: boom happened", text);
        Assert.Equal(UiNotificationLevel.Error, level);
    }

    [Fact]
    public void Schedule_failed_notice_without_summary_has_no_trailing_detail()
    {
        var (text, level) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", "Nightly", "task-9", ScheduleLifecycleKind.Failed, DateTimeOffset.UnixEpoch, null));

        Assert.Equal("Scheduled task Nightly failed", text);
        Assert.Equal(UiNotificationLevel.Error, level);
    }

    [Fact]
    public void Schedule_failed_summary_is_sanitized_single_line_and_bounded()
    {
        var noisy = "line one\nline two\twith tab\u001b[31mred" + new string('x', 400);
        var (text, _) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", "Nightly", "task-9", ScheduleLifecycleKind.Failed, DateTimeOffset.UnixEpoch, noisy));

        Assert.StartsWith("Scheduled task Nightly failed: ", text);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\u001b', text);
        // Bounded: the headline plus a capped, ellipsized summary — never the full 400+ char body.
        Assert.True(text.Length < 260, $"summary not bounded: {text.Length}");
    }

    [Fact]
    public void Schedule_stopped_notice_uses_warning()
    {
        var (text, level) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", "Nightly", "task-9", ScheduleLifecycleKind.Stopped, DateTimeOffset.UnixEpoch, null));

        Assert.Equal("Scheduled task Nightly stopped", text);
        Assert.Equal(UiNotificationLevel.Warning, level);
    }

    // ── Task 8 quality: model-controlled name/task id are sanitized (spoof-proof) ─

    // A hostile schedule name that tries to spoof extra transcript rows: an embedded newline, an ANSI
    // colour + screen-clear escape, a C0 BEL/NUL control, and bidi override/mark formatting controls.
    private const string SpoofName =
        "Deploy\n\u001b[31mFAKE ROW\u001b[0m\u202Espoofed\u0007\u0000\u200Etail";

    // A hostile managed task id carrying CR/LF, a clear-screen escape, and bidi controls.
    private const string SpoofTaskId = "job-7\r\n\u001b[2Jcleared\u202D\u0001end";

    [Fact]
    public void Schedule_started_notice_sanitizes_malicious_name_and_task_id()
    {
        var (text, level) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", SpoofName, SpoofTaskId, ScheduleLifecycleKind.Started, DateTimeOffset.UnixEpoch, null));

        AssertSingleSanitizedLine(text);
        Assert.StartsWith("Scheduled task ", text);
        Assert.Contains(" started as ", text);
        Assert.Contains("Deploy", text); // safe leading name text preserved
        Assert.Contains("job-7", text);  // safe leading task id text preserved
        Assert.Equal(UiNotificationLevel.Information, level);
    }

    [Fact]
    public void Schedule_completed_notice_sanitizes_malicious_name()
    {
        var (text, level) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", SpoofName, SpoofTaskId, ScheduleLifecycleKind.Completed, DateTimeOffset.UnixEpoch, "big result body"));

        AssertSingleSanitizedLine(text);
        Assert.StartsWith("Scheduled task ", text);
        Assert.EndsWith(" completed", text);
        Assert.Contains("Deploy", text);
        Assert.DoesNotContain("big result body", text); // result never surfaced on completion
        Assert.Equal(UiNotificationLevel.Information, level);
    }

    [Fact]
    public void Schedule_failed_notice_sanitizes_malicious_name_and_stays_bounded()
    {
        var noisy = "line one\nline two\u001b[31mred" + new string('x', 400);
        var (text, level) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", SpoofName, SpoofTaskId, ScheduleLifecycleKind.Failed, DateTimeOffset.UnixEpoch, noisy));

        AssertSingleSanitizedLine(text);
        Assert.StartsWith("Scheduled task ", text);
        Assert.Contains(" failed", text);
        Assert.Contains("Deploy", text);
        // Bounded even with a hostile name AND a 400+ char summary body.
        Assert.True(text.Length < 320, $"notice not bounded: {text.Length}");
        Assert.Equal(UiNotificationLevel.Error, level);
    }

    [Fact]
    public void Schedule_stopped_notice_sanitizes_malicious_name()
    {
        var (text, level) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", SpoofName, SpoofTaskId, ScheduleLifecycleKind.Stopped, DateTimeOffset.UnixEpoch, null));

        AssertSingleSanitizedLine(text);
        Assert.StartsWith("Scheduled task ", text);
        Assert.EndsWith(" stopped", text);
        Assert.Contains("Deploy", text);
        Assert.Equal(UiNotificationLevel.Warning, level);
    }

    [Fact]
    public void Schedule_notice_bounds_an_absurdly_long_name()
    {
        var (text, _) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", new string('n', 500), "task-9", ScheduleLifecycleKind.Completed, DateTimeOffset.UnixEpoch, null));

        AssertSingleSanitizedLine(text);
        Assert.True(text.Length < 160, $"name not bounded: {text.Length}");
    }

    [Fact]
    public void Schedule_notice_falls_back_to_definition_id_when_name_sanitizes_to_blank()
    {
        // A name made entirely of escape/control/bidi flattens to empty after sanitization.
        var (text, _) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", "\u001b[2J\u0007\u202E\n\t", "task-9", ScheduleLifecycleKind.Started, DateTimeOffset.UnixEpoch, null));

        AssertSingleSanitizedLine(text);
        Assert.Equal("Scheduled task def-1 started as task-9", text);
    }

    [Fact]
    public void Schedule_notice_falls_back_to_neutral_label_when_name_and_id_blank()
    {
        var (text, _) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "\u0007\u202E", "\n\t\u001b[0m", "task-9", ScheduleLifecycleKind.Completed, DateTimeOffset.UnixEpoch, null));

        AssertSingleSanitizedLine(text);
        Assert.Equal("Scheduled task schedule completed", text);
    }

    [Fact]
    public void Schedule_notice_preserves_printable_unicode_and_emoji_in_name()
    {
        // Sanitization must not double-escape or strip printable Unicode / emoji.
        var (text, _) = ReduceLifecycle(new ScheduleLifecycleEvent(
            "def-1", "Café 🚀 build", "task-9", ScheduleLifecycleKind.Completed, DateTimeOffset.UnixEpoch, null));

        Assert.Equal("Scheduled task Café 🚀 build completed", text);
    }

    private static void AssertSingleSanitizedLine(string text)
    {
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\t', text);
        Assert.DoesNotContain('\u001b', text);

        foreach (var ch in text)
        {
            Assert.False(char.IsControl(ch), $"control char U+{(int)ch:X4} survived: {text}");
        }

        foreach (var bidi in new[]
                 {
                     '\u061C', '\u202A', '\u202B', '\u202C', '\u202D', '\u202E',
                     '\u2066', '\u2067', '\u2068', '\u2069', '\u200E', '\u200F',
                 })
        {
            Assert.DoesNotContain(bidi, text);
        }
    }

    private static (string Text, UiNotificationLevel Level) ReduceLifecycle(ScheduleLifecycleEvent lifecycle)
    {
        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, new ScheduleLifecycleChangedEvent(lifecycle));

        // Exactly one new notice block is appended, and the mirrored notification matches it.
        var notice = Assert.IsType<NoticeTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.NotEqual(Guid.Empty, notice.Id);
        Assert.NotNull(state.Notification);
        Assert.Equal(notice.Text, state.Notification!.Message);
        Assert.Equal(notice.Level, state.Notification.Level);
        return (notice.Text, notice.Level);
    }
}
