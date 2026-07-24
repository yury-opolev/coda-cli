using System.Reflection;
using Coda.Agent;
using Coda.Sdk;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Mode;
using Coda.Tui.Ui.State;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class ToolActivityHistoryProjectorTests : IDisposable
{
    private readonly string workingDirectory = Path.Combine(
        AppContext.BaseDirectory,
        $"tool-activity-history-{Guid.NewGuid():N}");

    public ToolActivityHistoryProjectorTests()
    {
        Directory.CreateDirectory(this.workingDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.workingDirectory, recursive: true);
        }
        catch
        {
            // The test output directory may already have been removed by a failed test host.
        }
    }

    [Fact]
    public void Correlated_batches_project_as_one_completed_activity_with_call_order()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.UserText("before"),
            new(ChatRole.Assistant, [CorrelatedUse("call-a", "grep", """{"pattern":"a"}""")]),
            new(ChatRole.User, [CorrelatedResult("call-a", "A", ToolCallStatus.Succeeded)]),
            new(ChatRole.Assistant, [CorrelatedUse("call-b", "read_file", """{"path":"b"}""")]),
            new(ChatRole.User, [CorrelatedResult("call-b", "B", ToolCallStatus.Succeeded)]),
            new(ChatRole.Assistant, [new TextBlock("after")]),
        };

        var projected = SessionHistoryProjector.Project(history);

        var activity = Assert.Single(projected.OfType<ToolActivityTranscriptBlock>());
        Assert.Equal(["call-a", "call-b"], activity.Calls.Select(call => call.CallId).ToArray());
        Assert.Equal(["A", "B"], activity.Calls.Select(call => call.Result ?? string.Empty).ToArray());
        Assert.Equal(ToolActivityCompletionState.Completed, activity.CompletionState);
        Assert.Collection(
            projected,
            block => Assert.Equal("before", Assert.IsType<UserTranscriptBlock>(block).Text),
            block => Assert.Same(activity, block),
            block => Assert.Equal("after", Assert.IsType<AssistantTranscriptBlock>(block).Text));
    }

    [Fact]
    public void Correlated_results_match_the_exact_source_and_call_key()
    {
        const string root = "root";
        const string activityId = "activity";
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant,
            [
                CorrelatedUse("same-call", "root_tool", "{}", root, activityId, "root:root"),
                CorrelatedUse("same-call", "child_tool", "{}", root, activityId, "subagent:child"),
            ]),
            new(ChatRole.User,
            [
                CorrelatedResult("same-call", "child result", ToolCallStatus.Succeeded, root, activityId, "subagent:child"),
                CorrelatedResult("same-call", "wrong source", ToolCallStatus.Succeeded, root, activityId, "subagent:other"),
                CorrelatedResult("same-call", "root result", ToolCallStatus.Succeeded, root, activityId, "root:root"),
            ]),
        };

        var activity = Assert.Single(SessionHistoryProjector.Project(history).OfType<ToolActivityTranscriptBlock>());

        Assert.Collection(
            activity.Calls,
            call =>
            {
                Assert.Equal("root:root", call.SourceId);
                Assert.Equal("root_tool", call.ToolName);
                Assert.Equal("root result", call.Result);
            },
            call =>
            {
                Assert.Equal("subagent:child", call.SourceId);
                Assert.Equal("child_tool", call.ToolName);
                Assert.Equal("child result", call.Result);
            });
    }

    [Fact]
    public void Audit_adds_unrepresented_forwarded_call_without_duplicating_root_call()
    {
        const string root = "root";
        const string activityId = "activity";
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant,
            [
                CorrelatedUse("root-call", "root_tool", """{"from":"chat"}""", root, activityId, "root:root"),
            ]),
            new(ChatRole.User,
            [
                CorrelatedResult("root-call", "chat result", ToolCallStatus.Succeeded, root, activityId, "root:root"),
            ]),
        };
        var audit = new SessionAuditTurn
        {
            TurnIndex = 0,
            TsUtc = DateTime.UtcNow,
            Provider = "test",
            Model = "test",
            InputTokens = 1,
            OutputTokens = 1,
            ToolCalls =
            [
                new ToolCallRecord("root_tool", """{"from":"audit"}""", "audit result", false)
                {
                    RootTurnId = root,
                    ActivityId = activityId,
                    CallId = "root-call",
                    SourceId = "root:root",
                    Status = ToolCallStatus.Succeeded,
                },
                new ToolCallRecord("child_tool", """{"from":"child"}""", "child result", false)
                {
                    RootTurnId = root,
                    ActivityId = activityId,
                    CallId = "child-call",
                    SourceId = "subagent:child",
                    Status = ToolCallStatus.Succeeded,
                },
            ],
        };

        var activity = Assert.Single(
            SessionHistoryProjector.Project(history, [audit]).OfType<ToolActivityTranscriptBlock>());

        Assert.Equal(2, activity.Calls.Length);
        Assert.Equal(["root-call", "child-call"], activity.Calls.Select(call => call.CallId).ToArray());
        Assert.Equal("chat result", activity.Calls[0].Result);
        Assert.Equal("subagent:child", activity.Calls[1].SourceId);
        Assert.Equal("""{"from":"child"}""", activity.Calls[1].InputJson);
        Assert.Equal("child result", activity.Calls[1].Result);
        Assert.Equal(ToolCallStatus.Succeeded, activity.Calls[1].Status);
    }

    [Fact]
    public void Legacy_repeated_call_ids_in_separate_exchanges_create_two_activities()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new ToolUseBlock("same", "grep", """{"pattern":"a"}""")]),
            new(ChatRole.User, [new ToolResultBlock("same", "A")]),
            new(ChatRole.Assistant, [new TextBlock("between")]),
            new(ChatRole.Assistant, [new ToolUseBlock("same", "grep", """{"pattern":"b"}""")]),
            new(ChatRole.User, [new ToolResultBlock("same", "B")]),
        };

        var activities = SessionHistoryProjector.Project(history)
            .OfType<ToolActivityTranscriptBlock>()
            .ToArray();

        Assert.Equal(2, activities.Length);
        Assert.NotEqual(activities[0].RootTurnId, activities[1].RootTurnId);
        Assert.Equal("A", Assert.Single(activities[0].Calls).Result);
        Assert.Equal("B", Assert.Single(activities[1].Calls).Result);
    }

    [Fact]
    public void Contiguous_legacy_tool_uses_are_grouped_into_one_activity()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant,
            [
                new ToolUseBlock("first", "grep", """{"pattern":"a"}"""),
                new ToolUseBlock("second", "read_file", """{"path":"b"}"""),
            ]),
            new(ChatRole.User,
            [
                new ToolResultBlock("first", "A"),
                new ToolResultBlock("second", "B"),
            ]),
        };

        var activity = Assert.Single(SessionHistoryProjector.Project(history).OfType<ToolActivityTranscriptBlock>());

        Assert.Equal(["first", "second"], activity.Calls.Select(call => call.CallId).ToArray());
        Assert.Equal(["A", "B"], activity.Calls.Select(call => call.Result ?? string.Empty).ToArray());
        Assert.Equal(ToolActivityCompletionState.Completed, activity.CompletionState);
    }

    [Fact]
    public void Interleaved_legacy_tool_uses_attach_following_results_to_their_activities()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant,
            [
                new TextBlock("before"),
                new ToolUseBlock("call-a", "grep", """{"pattern":"a"}"""),
                new TextBlock("between"),
                new ToolUseBlock("call-b", "read_file", """{"path":"b"}"""),
            ]),
            new(ChatRole.User,
            [
                new ToolResultBlock("unknown", "ignored"),
                new ToolResultBlock("call-a", "A"),
                new ToolResultBlock("call-b", "B"),
            ]),
        };

        var projected = SessionHistoryProjector.Project(history);
        var activities = projected.OfType<ToolActivityTranscriptBlock>().ToArray();

        Assert.Collection(
            projected,
            block => Assert.Equal("before", Assert.IsType<AssistantTranscriptBlock>(block).Text),
            block => Assert.Same(activities[0], block),
            block => Assert.Equal("between", Assert.IsType<AssistantTranscriptBlock>(block).Text),
            block => Assert.Same(activities[1], block));
        Assert.Equal(["call-a", "call-b"], activities.Select(activity => Assert.Single(activity.Calls).CallId).ToArray());
        Assert.Equal(
            ["A", "B"],
            activities.Select(activity => Assert.Single(activity.Calls).Result ?? string.Empty).ToArray());
        Assert.All(activities, activity => Assert.Equal(ToolActivityCompletionState.Completed, activity.CompletionState));
        Assert.All(
            activities.Select(activity => Assert.Single(activity.Calls)),
            call => Assert.Equal(ToolCallStatus.Succeeded, call.Status));
    }

    [Fact]
    public void Partial_correlation_metadata_falls_back_to_legacy_without_merging()
    {
        var partialUse = new ToolUseBlock("partial-call", "grep", """{"pattern":"partial"}""")
        {
            RootTurnId = "partial-root",
        };
        var partialResult = new ToolResultBlock("partial-call", "partial result")
        {
            RootTurnId = "partial-root",
        };
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant,
            [
                partialUse,
                CorrelatedUse("correlated-call", "read_file", "{}", "root", "activity", "root:root"),
            ]),
            new(ChatRole.User,
            [
                partialResult,
                CorrelatedResult("correlated-call", "correlated result", ToolCallStatus.Succeeded),
            ]),
        };

        var activities = SessionHistoryProjector.Project(history)
            .OfType<ToolActivityTranscriptBlock>()
            .ToArray();

        Assert.Equal(2, activities.Length);
        Assert.Equal("partial-call", Assert.Single(activities[0].Calls).CallId);
        Assert.Equal("partial result", Assert.Single(activities[0].Calls).Result);
        Assert.Equal(ToolCallStatus.Succeeded, Assert.Single(activities[0].Calls).Status);
        Assert.Equal("correlated-call", Assert.Single(activities[1].Calls).CallId);
        Assert.Equal("correlated result", Assert.Single(activities[1].Calls).Result);
    }

    [Fact]
    public void Legacy_call_without_a_terminal_result_is_cancelled_in_historical_output()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new ToolUseBlock("unfinished", "grep", "{}")]),
        };

        var activity = Assert.Single(SessionHistoryProjector.Project(history).OfType<ToolActivityTranscriptBlock>());
        var call = Assert.Single(activity.Calls);

        Assert.Equal(ToolCallStatus.Cancelled, call.Status);
        Assert.Equal(ToolActivityCompletionState.Cancelled, activity.CompletionState);
        Assert.Null(call.Result);
    }

    [Fact]
    public void Invalid_or_missing_persisted_status_falls_back_to_the_result()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant,
            [
                CorrelatedUse("ok", "read_file", "{}"),
                CorrelatedUse("error", "read_file", "{}"),
            ]),
            new(ChatRole.User,
            [
                CorrelatedResult("ok", "ok result", "not-a-status"),
                CorrelatedResult("error", "error result", status: null, isError: true),
            ]),
        };

        var activity = Assert.Single(SessionHistoryProjector.Project(history).OfType<ToolActivityTranscriptBlock>());

        Assert.Equal(ToolCallStatus.Succeeded, activity.Calls[0].Status);
        Assert.Equal("ok result", activity.Calls[0].Result);
        Assert.Equal(ToolCallStatus.Failed, activity.Calls[1].Status);
        Assert.Equal("error result", activity.Calls[1].Error);
        Assert.Equal(ToolActivityCompletionState.Completed, activity.CompletionState);
    }

    [Fact]
    public void Normal_transcript_blocks_remain_in_order_around_a_historical_activity()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.UserText("user before"),
            new(ChatRole.Assistant, [new TextBlock("assistant before")]),
            new(ChatRole.Assistant, [CorrelatedUse("call", "grep", "{}")]),
            new(ChatRole.User, [CorrelatedResult("call", "result", ToolCallStatus.Succeeded)]),
            new(ChatRole.Assistant, [new TextBlock("assistant after")]),
            ChatMessage.UserText("user after"),
        };

        var projected = SessionHistoryProjector.Project(history);

        Assert.Collection(
            projected,
            block => Assert.Equal("user before", Assert.IsType<UserTranscriptBlock>(block).Text),
            block => Assert.Equal("assistant before", Assert.IsType<AssistantTranscriptBlock>(block).Text),
            block => Assert.IsType<ToolActivityTranscriptBlock>(block),
            block => Assert.Equal("assistant after", Assert.IsType<AssistantTranscriptBlock>(block).Text),
            block => Assert.Equal("user after", Assert.IsType<UserTranscriptBlock>(block).Text));
    }

    [Fact]
    public async Task Resume_command_loads_audit_turns_and_seeds_grouped_activity()
    {
        const string sessionId = "resume-audit";
        const string root = "root";
        const string activityId = "activity";
        await new SessionTranscriptStore(this.workingDirectory).SaveAsync(
            sessionId,
            [new ChatMessage(ChatRole.Assistant, [CorrelatedUse("root-call", "root_tool", "{}", root, activityId, "root:root")])]);
        await new SessionAuditStore(this.workingDirectory).AppendTurnAsync(
            sessionId,
            AuditTurn(
                0,
                new ToolCallRecord("child_tool", """{"path":"child"}""", "child result", false)
                {
                    RootTurnId = root,
                    ActivityId = activityId,
                    CallId = "child-call",
                    SourceId = "subagent:child",
                    Status = ToolCallStatus.Succeeded,
                }));
        var events = new RecordingUiEvents();
        var context = this.CreateCommandContext(events);

        await new ResumeCommand().ExecuteAsync(context, [sessionId]);

        var seeded = Assert.Single(events.Events.OfType<TranscriptSeededEvent>());
        var activity = Assert.Single(seeded.Blocks.OfType<ToolActivityTranscriptBlock>());
        Assert.Equal(["root-call", "child-call"], activity.Calls.Select(call => call.CallId).ToArray());
    }

    [Fact]
    public async Task Resume_command_handles_an_absent_audit_sidecar()
    {
        const string sessionId = "resume-no-audit";
        await new SessionTranscriptStore(this.workingDirectory).SaveAsync(
            sessionId,
            [ChatMessage.UserText("still loads")]);
        var events = new RecordingUiEvents();
        var context = this.CreateCommandContext(events);

        await new ResumeCommand().ExecuteAsync(context, [sessionId]);

        var seeded = Assert.Single(events.Events.OfType<TranscriptSeededEvent>());
        Assert.Equal("still loads", Assert.IsType<UserTranscriptBlock>(Assert.Single(seeded.Blocks)).Text);
    }

    [Fact]
    public async Task Interactive_startup_loads_audit_turns_from_the_resumed_session_directory()
    {
        const string sessionId = "startup-audit";
        const string root = "root";
        const string activityId = "activity";
        await new SessionTranscriptStore(this.workingDirectory).SaveAsync(
            sessionId,
            [new ChatMessage(ChatRole.Assistant, [CorrelatedUse("root-call", "root_tool", "{}", root, activityId, "root:root")])]);
        await new SessionAuditStore(this.workingDirectory).AppendTurnAsync(
            sessionId,
            AuditTurn(
                0,
                new ToolCallRecord("child_tool", "{}", "child result", false)
                {
                    RootTurnId = root,
                    ActivityId = activityId,
                    CallId = "child-call",
                    SourceId = "subagent:child",
                    Status = ToolCallStatus.Succeeded,
                }));
        var context = this.CreateCommandContext();
        using var mailbox = new UiEventMailbox(8);
        var seed = typeof(DefaultInteractiveSessionRunner).GetMethod(
            "SeedSessionAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(seed);

        var task = Assert.IsAssignableFrom<Task>(seed!.Invoke(
            null,
            [context, new TuiLaunchOptions(TuiPreference.Auto, false, ["--resume", sessionId], null), mailbox, CancellationToken.None]));
        await task;

        var events = new List<UiEvent>();
        while (mailbox.TryRead(out var uiEvent))
        {
            events.Add(uiEvent!);
        }

        var seeded = Assert.Single(events.OfType<TranscriptSeededEvent>());
        var activity = Assert.Single(seeded.Blocks.OfType<ToolActivityTranscriptBlock>());
        Assert.Equal(["root-call", "child-call"], activity.Calls.Select(call => call.CallId).ToArray());
    }

    [Fact]
    public void Audit_only_activities_are_appended_after_normal_transcript_in_audit_order()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.UserText("first user"),
            new(ChatRole.Assistant, [new TextBlock("first tool iteration")]),
            new(ChatRole.User, [new ToolResultBlock("iteration-tool", "done")]),
            new(ChatRole.Assistant, [new TextBlock("first final")]),
            ChatMessage.UserText("second user"),
            new(ChatRole.Assistant, [new TextBlock("second assistant")]),
        };
        SessionAuditTurn[] audit =
        [
            AuditTurn(
                1,
                new ToolCallRecord("first_forwarded", "{}", "first done", false)
                {
                    RootTurnId = "audit-root-1",
                    ActivityId = "audit-activity-1",
                    CallId = "first-forwarded-call",
                    SourceId = "subagent:first",
                    Status = ToolCallStatus.Succeeded,
                }),
            AuditTurn(
                7,
                new ToolCallRecord("second_forwarded", "{}", "second done", false)
                {
                    RootTurnId = "audit-root-2",
                    ActivityId = "audit-activity-2",
                    CallId = "second-forwarded-call",
                    SourceId = "subagent:second",
                    Status = ToolCallStatus.Succeeded,
                }),
        ];

        var projected = SessionHistoryProjector.Project(history, audit);

        Assert.Collection(
            projected,
            block => Assert.Equal("first user", Assert.IsType<UserTranscriptBlock>(block).Text),
            block => Assert.Equal("first tool iteration", Assert.IsType<AssistantTranscriptBlock>(block).Text),
            block => Assert.Equal("first final", Assert.IsType<AssistantTranscriptBlock>(block).Text),
            block => Assert.Equal("second user", Assert.IsType<UserTranscriptBlock>(block).Text),
            block => Assert.Equal("second assistant", Assert.IsType<AssistantTranscriptBlock>(block).Text),
            block => Assert.Equal(
                "first-forwarded-call",
                Assert.Single(Assert.IsType<ToolActivityTranscriptBlock>(block).Calls).CallId),
            block => Assert.Equal(
                "second-forwarded-call",
                Assert.Single(Assert.IsType<ToolActivityTranscriptBlock>(block).Calls).CallId));
    }

    private CommandContext CreateCommandContext(RecordingUiEvents? events = null)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        var credentials = new CredentialManager(
            new InMemoryTokenStore(),
            new ICredentialProvider[] { new ClaudeAiProvider() });
        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };
        var session = new SessionState("claude-ai", this.workingDirectory);
        var commands = new SlashCommandRegistry([new ResumeCommand()]);

        return new CommandContext(console, credentials, session, providers, commands, events: events);
    }

    private static ToolUseBlock CorrelatedUse(
        string callId,
        string name,
        string input,
        string root = "root",
        string activityId = "activity",
        string source = "root:root") =>
        new(callId, name, input)
        {
            RootTurnId = root,
            ActivityId = activityId,
            SourceId = source,
        };

    private static ToolResultBlock CorrelatedResult(
        string callId,
        string content,
        ToolCallStatus status,
        string root = "root",
        string activityId = "activity",
        string source = "root:root",
        bool isError = false) =>
        CorrelatedResult(callId, content, status.ToString(), root, activityId, source, isError);

    private static ToolResultBlock CorrelatedResult(
        string callId,
        string content,
        string? status,
        string root = "root",
        string activityId = "activity",
        string source = "root:root",
        bool isError = false) =>
        new(callId, content, isError)
        {
            RootTurnId = root,
            ActivityId = activityId,
            SourceId = source,
            ToolStatus = status,
        };

    private static SessionAuditTurn AuditTurn(int turnIndex, params ToolCallRecord[] calls) =>
        new()
        {
            TurnIndex = turnIndex,
            TsUtc = DateTime.UtcNow,
            Provider = "test",
            Model = "test",
            InputTokens = 1,
            OutputTokens = 1,
            ToolCalls = calls,
        };
}
