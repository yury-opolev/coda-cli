using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Hooks;
using LlmClient;

namespace Engine.Tests;

public sealed class AgentToolIdentityTests
{
    private sealed record ObservedEvent(
        string Kind,
        ToolCallIdentity Identity,
        string ToolName,
        ToolCallStatus? Status = null);

    private sealed class CorrelatedSink : IAgentSink
    {
        private readonly List<ObservedEvent> events = [];
        private readonly List<string> timeline = [];

        public IReadOnlyList<ObservedEvent> Events
        {
            get { lock (this.events) { return this.events.ToArray(); } }
        }

        public IReadOnlyList<string> Timeline
        {
            get { lock (this.events) { return this.timeline.ToArray(); } }
        }

        public void OnAssistantText(string delta) { }

        public void OnAssistantTextComplete() { }

        public void OnToolCall(string toolName, string inputJson) { }

        public void OnToolResult(string toolName, ToolResult result) { }

        public void OnError(string message) { }

        public void OnToolQueued(ToolCallIdentity identity, string toolName, string inputJson) =>
            this.Record("queued", identity, toolName);

        public void OnToolCall(ToolCallIdentity identity, string toolName, string inputJson) =>
            this.Record("call", identity, toolName);

        public void OnToolStatus(ToolCallIdentity identity, string toolName, ToolCallStatus status) =>
            this.Record("status", identity, toolName, status);

        public void OnToolResult(ToolCallIdentity identity, string toolName, ToolResult result, ToolCallStatus status) =>
            this.Record("result", identity, toolName, status);

        public void OnToolProgress(ToolCallIdentity identity, string toolName, long elapsedMs) =>
            this.Record("progress", identity, toolName);

        public void Mark(string marker)
        {
            lock (this.events)
            {
                this.timeline.Add(marker);
            }
        }

        private void Record(string kind, ToolCallIdentity identity, string toolName, ToolCallStatus? status = null)
        {
            lock (this.events)
            {
                this.events.Add(new ObservedEvent(kind, identity, toolName, status));
                this.timeline.Add(status is { } value
                    ? $"{kind}:{value}:{identity.CallId}"
                    : $"{kind}:{identity.CallId}");
            }
        }
    }

    private sealed class LegacySink : IAgentSink
    {
        public int ToolCalls { get; private set; }

        public int ToolResults { get; private set; }

        public void OnAssistantText(string delta) { }

        public void OnAssistantTextComplete() { }

        public void OnToolCall(string toolName, string inputJson) => this.ToolCalls++;

        public void OnToolResult(string toolName, ToolResult result) => this.ToolResults++;

        public void OnError(string message) { }
    }

    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = turns[this.turn++];
            foreach (var streamEvent in events)
            {
                await Task.Yield();
                yield return streamEvent;
            }
        }
    }

    private sealed class DelegateTool(
        string name,
        bool isReadOnly,
        Func<JsonElement, ToolContext, CancellationToken, Task<ToolResult>> execute) : ITool
    {
        public int Calls { get; private set; }

        public string Name => name;

        public string Description => name;

        public string InputSchemaJson => "{\"type\":\"object\"}";

        public bool IsReadOnly => isReadOnly;

        public async Task<ToolResult> ExecuteAsync(
            JsonElement input,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return await execute(input, context, cancellationToken);
        }
    }

    private sealed class RecordingPrompt(CorrelatedSink sink, bool allowed) : IPermissionPrompt
    {
        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
        {
            sink.Mark("prompt");
            return Task.FromResult(allowed);
        }
    }

    private sealed class ThrowingPrompt : IPermissionPrompt
    {
        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(new InvalidOperationException("prompt failed"));
    }

    private static AgentOptions Options() => new()
    {
        SystemPrompt = "sys",
        WorkingDirectory = ".",
        Model = "model",
    };

    private static List<ChatMessage> History() => [ChatMessage.UserText("go")];

    private static IReadOnlyList<AssistantStreamEvent> ToolTurn(params ToolUseBlock[] toolUses)
    {
        var events = new List<AssistantStreamEvent>(toolUses.Length + 1);
        events.AddRange(toolUses.Select(AssistantStreamEvent.Tool));
        events.Add(AssistantStreamEvent.Finished("tool_use"));
        return events;
    }

    private static IReadOnlyList<AssistantStreamEvent> EndTurn() =>
        [AssistantStreamEvent.Finished("end_turn")];

    private static DelegateTool SuccessTool(string name, bool isReadOnly = true) =>
        new(name, isReadOnly, (_, _, _) => Task.FromResult(new ToolResult("ok")));

    [Fact]
    public async Task Model_tool_batches_share_one_activity_and_preserve_provider_ids_and_root_source()
    {
        var configuredRoot = new ToolActivityContext("root-turn", "root:configured");
        var sink = new CorrelatedSink();
        var loop = new AgentLoop(
            new ScriptedClient(
                ToolTurn(new ToolUseBlock("call-1", "first", "{}")),
                ToolTurn(new ToolUseBlock("call-2", "second", "{}")),
                EndTurn()),
            new ToolRegistry([SuccessTool("first"), SuccessTool("second")]),
            new AllowAllPermissionPrompt(),
            Options(),
            toolActivity: configuredRoot);

        await loop.RunAsync(History(), sink);

        var queued = sink.Events.Where(@event => @event.Kind == "queued").ToArray();
        Assert.Equal(["call-1", "call-2"], queued.Select(@event => @event.Identity.CallId));
        Assert.All(queued, @event =>
        {
            Assert.Equal("root-turn", @event.Identity.RootTurnId);
            Assert.Equal("root:configured", @event.Identity.SourceId);
        });
        Assert.Single(queued.Select(@event => @event.Identity.ActivityId).Distinct());
    }

    [Fact]
    public async Task Reused_loop_keeps_configured_root_and_source_but_starts_a_new_activity_per_run()
    {
        var configuredRoot = new ToolActivityContext("root-turn", "root:configured");
        var sink = new CorrelatedSink();
        var loop = new AgentLoop(
            new ScriptedClient(
                ToolTurn(new ToolUseBlock("first-run", "echo", "{}")),
                EndTurn(),
                ToolTurn(new ToolUseBlock("second-run", "echo", "{}")),
                EndTurn()),
            new ToolRegistry([SuccessTool("echo")]),
            new AllowAllPermissionPrompt(),
            Options(),
            toolActivity: configuredRoot);
        var history = History();

        await loop.RunAsync(history, sink);
        history.Add(ChatMessage.UserText("again"));
        await loop.RunAsync(history, sink);

        var calls = sink.Events.Where(@event => @event.Kind == "queued").ToArray();
        Assert.Equal(["first-run", "second-run"], calls.Select(@event => @event.Identity.CallId));
        Assert.All(calls, @event =>
        {
            Assert.Equal("root-turn", @event.Identity.RootTurnId);
            Assert.Equal("root:configured", @event.Identity.SourceId);
        });
        Assert.NotEqual(calls[0].Identity.ActivityId, calls[1].Identity.ActivityId);
    }

    [Fact]
    public async Task Entire_batch_is_queued_in_provider_order_before_any_tool_starts()
    {
        var sink = new CorrelatedSink();
        var loop = new AgentLoop(
            new ScriptedClient(
                ToolTurn(
                    new ToolUseBlock("first-call", "first", "{}"),
                    new ToolUseBlock("second-call", "second", "{}")),
                EndTurn()),
            new ToolRegistry([SuccessTool("first"), SuccessTool("second")]),
            new AllowAllPermissionPrompt(),
            Options());

        await loop.RunAsync(History(), sink);

        var events = sink.Events;
        var firstStart = Array.FindIndex(
            events.ToArray(),
            @event => @event.Kind == "call"
                || (@event.Kind == "status" && @event.Status == ToolCallStatus.Running));

        Assert.True(firstStart >= 2);
        Assert.Equal(
            ["first-call", "second-call"],
            events.Take(firstStart)
                .Where(@event => @event.Kind == "queued")
                .Select(@event => @event.Identity.CallId));
    }

    [Fact]
    public async Task Successful_tool_transitions_from_queued_to_call_to_running_to_succeeded()
    {
        var sink = new CorrelatedSink();
        var loop = new AgentLoop(
            new ScriptedClient(ToolTurn(new ToolUseBlock("success-call", "echo", "{}")), EndTurn()),
            new ToolRegistry([SuccessTool("echo")]),
            new AllowAllPermissionPrompt(),
            Options());

        await loop.RunAsync(History(), sink);

        var transitions = sink.Events
            .Where(@event => @event.Identity.CallId == "success-call")
            .Select(@event => @event.Status?.ToString() ?? @event.Kind)
            .ToArray();

        Assert.Equal(["queued", "call", "Running", "Succeeded"], transitions);
    }

    [Fact]
    public async Task Approval_status_precedes_the_permission_prompt_then_execution_and_result()
    {
        var sink = new CorrelatedSink();
        var loop = new AgentLoop(
            new ScriptedClient(ToolTurn(new ToolUseBlock("approval-call", "write", "{}")), EndTurn()),
            new ToolRegistry([SuccessTool("write", isReadOnly: false)]),
            new RecordingPrompt(sink, allowed: true),
            Options());

        await loop.RunAsync(History(), sink);

        Assert.Equal(
            [
                "queued:approval-call",
                "call:approval-call",
                "status:AwaitingApproval:approval-call",
                "prompt",
                "status:Running:approval-call",
                "result:Succeeded:approval-call",
            ],
            sink.Timeline);
    }

    [Fact]
    public async Task Permission_prompt_exception_becomes_one_failed_result_and_provider_history_block()
    {
        var sink = new CorrelatedSink();
        var history = History();
        var loop = new AgentLoop(
            new ScriptedClient(ToolTurn(new ToolUseBlock("prompt-error-id", "write", "{}")), EndTurn()),
            new ToolRegistry([SuccessTool("write", isReadOnly: false)]),
            new ThrowingPrompt(),
            Options());

        var exception = await Record.ExceptionAsync(() => loop.RunAsync(history, sink));

        Assert.Null(exception);
        var terminal = Assert.Single(sink.Events, @event =>
            @event.Kind == "result" && @event.Identity.CallId == "prompt-error-id");
        Assert.Equal(ToolCallStatus.Failed, terminal.Status);
        var historyResult = Assert.Single(history.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal("prompt-error-id", historyResult.ToolUseId);
        Assert.True(historyResult.IsError);
    }

    [Fact]
    public async Task Failed_tool_branches_emit_one_failed_terminal_result_with_the_provider_id()
    {
        var denied = SuccessTool("denied", isReadOnly: false);
        var blocked = SuccessTool("blocked");
        var throws = new DelegateTool("throws", true, (_, _, _) => throw new InvalidOperationException("boom"));
        var error = new DelegateTool("error", true, (_, _, _) => Task.FromResult(new ToolResult("reported error", IsError: true)));
        var timeout = new DelegateTool(
            "timeout",
            true,
            async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ToolResult("unreachable");
            });
        var sink = new CorrelatedSink();
        var userHooks = new UserHookRunner(
            [new UserHook("PreToolUse", "block", Matcher: "blocked")],
            execOverride: (_, _, _) => Task.FromResult((1, "blocked by test")));
        var loop = new AgentLoop(
            new ScriptedClient(
                ToolTurn(
                    new ToolUseBlock("unknown-id", "unknown", "{}"),
                    new ToolUseBlock("denied-id", "denied", "{}"),
                    new ToolUseBlock("blocked-id", "blocked", "{}"),
                    new ToolUseBlock("throws-id", "throws", "{}"),
                    new ToolUseBlock("error-id", "error", "{}"),
                    new ToolUseBlock("timeout-id", "timeout", "{}")),
                EndTurn()),
            new ToolRegistry([denied, blocked, throws, error, timeout]),
            new RecordingPrompt(sink, allowed: false),
            Options(),
            userHooks: userHooks,
            toolMaxDuration: TimeSpan.FromMilliseconds(50));

        await loop.RunAsync(History(), sink);

        var ids = new[] { "unknown-id", "denied-id", "blocked-id", "throws-id", "error-id", "timeout-id" };
        foreach (var id in ids)
        {
            var terminal = Assert.Single(sink.Events, @event =>
                @event.Kind == "result" && @event.Identity.CallId == id);
            Assert.Equal(ToolCallStatus.Failed, terminal.Status);
            Assert.Equal(id, terminal.Identity.CallId);
        }

        Assert.Equal(0, denied.Calls);
        Assert.Equal(0, blocked.Calls);
        Assert.DoesNotContain(sink.Events, @event =>
            @event.Identity.CallId is "unknown-id" or "denied-id" or "blocked-id"
            && @event.Status == ToolCallStatus.Running);
    }

    [Fact]
    public async Task Steering_skips_remaining_queued_calls_without_starting_them()
    {
        var steering = new SteeringInbox();
        var first = new DelegateTool(
            "first",
            true,
            (_, _, _) =>
            {
                steering.Enqueue("redirect");
                return Task.FromResult(new ToolResult("first complete"));
            });
        var skipped = SuccessTool("skipped");
        var sink = new CorrelatedSink();
        var loop = new AgentLoop(
            new ScriptedClient(
                ToolTurn(
                    new ToolUseBlock("first-id", "first", "{}"),
                    new ToolUseBlock("skipped-id", "skipped", "{}")),
                EndTurn()),
            new ToolRegistry([first, skipped]),
            new AllowAllPermissionPrompt(),
            Options(),
            steering: steering);

        await loop.RunAsync(History(), sink);

        var skippedEvents = sink.Events.Where(@event => @event.Identity.CallId == "skipped-id").ToArray();
        Assert.Collection(
            skippedEvents,
            @event => Assert.Equal("queued", @event.Kind),
            @event =>
            {
                Assert.Equal("result", @event.Kind);
                Assert.Equal(ToolCallStatus.Skipped, @event.Status);
            });
        Assert.Equal(0, skipped.Calls);
        Assert.DoesNotContain(skippedEvents, @event =>
            @event.Kind == "call" || @event.Status == ToolCallStatus.Running);
    }

    [Fact]
    public async Task Committed_tool_history_blocks_keep_correlation_and_terminal_status()
    {
        var root = new ToolActivityContext("root-turn", "root:root-turn");
        var steering = new SteeringInbox();
        var first = new DelegateTool(
            "first",
            true,
            (_, _, _) =>
            {
                steering.Enqueue("redirect");
                return Task.FromResult(new ToolResult("first complete"));
            });
        var history = History();
        var loop = new AgentLoop(
            new ScriptedClient(
                ToolTurn(
                    new ToolUseBlock("unknown-id", "unknown", "{}"),
                    new ToolUseBlock("first-id", "first", "{}"),
                    new ToolUseBlock("skipped-id", "skipped", "{}")),
                EndTurn()),
            new ToolRegistry([first]),
            new AllowAllPermissionPrompt(),
            Options(),
            steering: steering,
            toolActivity: root);

        await loop.RunAsync(history, new CorrelatedSink());

        var toolUses = history.SelectMany(message => message.Content).OfType<ToolUseBlock>().ToArray();
        var results = history.SelectMany(message => message.Content).OfType<ToolResultBlock>().ToArray();
        Assert.Equal(["unknown-id", "first-id", "skipped-id"], toolUses.Select(block => block.Id));
        Assert.All(toolUses, block =>
        {
            Assert.Equal("root-turn", block.RootTurnId);
            Assert.NotNull(block.ActivityId);
            Assert.Equal("root:root-turn", block.SourceId);
        });
        Assert.Single(toolUses.Select(block => block.ActivityId).Distinct());

        Assert.Collection(
            results,
            block =>
            {
                Assert.Equal("unknown-id", block.ToolUseId);
                Assert.Equal("Failed", block.ToolStatus);
            },
            block =>
            {
                Assert.Equal("first-id", block.ToolUseId);
                Assert.Equal("Succeeded", block.ToolStatus);
            },
            block =>
            {
                Assert.Equal("skipped-id", block.ToolUseId);
                Assert.Equal("Skipped", block.ToolStatus);
            });
        Assert.All(results, block =>
        {
            Assert.Equal("root-turn", block.RootTurnId);
            Assert.Equal(toolUses[0].ActivityId, block.ActivityId);
            Assert.Equal("root:root-turn", block.SourceId);
        });
    }

    [Fact]
    public async Task Progress_heartbeats_keep_the_correct_identity_for_repeated_tool_names()
    {
        var slow = new DelegateTool(
            "slow",
            true,
            async (_, _, cancellationToken) =>
            {
                await Task.Delay(40, cancellationToken);
                return new ToolResult("done");
            });
        var sink = new CorrelatedSink();
        var loop = new AgentLoop(
            new ScriptedClient(
                ToolTurn(
                    new ToolUseBlock("slow-one", "slow", "{}"),
                    new ToolUseBlock("slow-two", "slow", "{}")),
                EndTurn()),
            new ToolRegistry([slow]),
            new AllowAllPermissionPrompt(),
            Options(),
            toolProgressInterval: TimeSpan.FromMilliseconds(5));

        await loop.RunAsync(History(), sink);

        var progress = sink.Events.Where(@event => @event.Kind == "progress").ToArray();
        Assert.Contains(progress, @event => @event.Identity.CallId == "slow-one");
        Assert.Contains(progress, @event => @event.Identity.CallId == "slow-two");
        Assert.All(progress, @event => Assert.Equal("slow", @event.ToolName));
    }

    [Fact]
    public async Task Tool_context_receives_the_current_activity_identity_and_source()
    {
        ToolActivityContext? received = null;
        var root = new ToolActivityContext("root-turn", "subagent:task-42");
        var sink = new CorrelatedSink();
        var probe = new DelegateTool(
            "probe",
            true,
            (_, context, _) =>
            {
                received = context.ToolActivity;
                return Task.FromResult(new ToolResult("ok"));
            });
        var loop = new AgentLoop(
            new ScriptedClient(ToolTurn(new ToolUseBlock("probe-id", "probe", "{}")), EndTurn()),
            new ToolRegistry([probe]),
            new AllowAllPermissionPrompt(),
            Options(),
            toolActivity: root);

        await loop.RunAsync(History(), sink);

        var identity = Assert.Single(sink.Events, @event => @event.Kind == "call").Identity;
        Assert.NotNull(received);
        Assert.Equal(identity.RootTurnId, received!.RootTurnId);
        Assert.Equal(identity.ActivityId, received.ActivityId);
        Assert.Equal(identity.SourceId, received.SourceId);
    }

    [Fact]
    public async Task Empty_batches_emit_no_correlated_tool_events()
    {
        var sink = new CorrelatedSink();
        var loop = new AgentLoop(
            new ScriptedClient(EndTurn()),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options());

        await loop.RunAsync(History(), sink);

        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task Legacy_sink_receives_each_tool_call_and_result_once()
    {
        var sink = new LegacySink();
        var loop = new AgentLoop(
            new ScriptedClient(ToolTurn(new ToolUseBlock("legacy-id", "echo", "{}")), EndTurn()),
            new ToolRegistry([SuccessTool("echo")]),
            new AllowAllPermissionPrompt(),
            Options());

        await loop.RunAsync(History(), sink);

        Assert.Equal(1, sink.ToolCalls);
        Assert.Equal(1, sink.ToolResults);
    }
}
