using System.Collections.Immutable;
using System.Reflection;
using Coda.Agent;
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Lsp;
using Coda.Mcp;
using Coda.Sdk;
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
            ]);

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
    public void Null_publisher_is_a_no_op_singleton()
    {
        Assert.Same(NullUiEventPublisher.Instance, NullUiEventPublisher.Instance);
        NullUiEventPublisher.Instance.Publish(new AssistantTextCompletedEvent());
    }
}
