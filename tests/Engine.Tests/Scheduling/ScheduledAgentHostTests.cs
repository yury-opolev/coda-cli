using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Agent.Hooks;
using Coda.Agent.Lsp;
using Coda.Agent.Scheduling;
using Coda.Agent.Settings;
using Coda.Agent.Tasks;
using Coda.Agent.ToolSearch;
using Coda.Agent.Tools;
using Coda.Sdk;
using Coda.Sdk.Scheduling;
using Coda.Sdk.Turns;
using Engine.Tests.TestSupport;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Microsoft.Extensions.Logging.Abstractions;
using static Engine.Tests.TestSupport.CredentialFixtures;

namespace Engine.Tests.Scheduling;

/// <summary>
/// Coverage for the isolated scheduled-agent host path: the <see cref="TurnPipelineBuilder"/>
/// scheduled spec assembly and the <see cref="ScheduledAgentHost"/> that runs a scheduled root
/// with its own history using the live session options, permissions, and shared collaborators.
/// </summary>
public sealed class ScheduledAgentHostTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_sched_host_").FullName;

    // ---- Fakes -----------------------------------------------------------------------------

    /// <summary>Yields pre-baked turns in order across successive StreamAsync calls.</summary>
    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient, IDisposable
    {
        private int turn;

        public string ProviderId => ClaudeAiProvider.Id;

        public bool Disposed { get; private set; }

        public void Dispose() => this.Disposed = true;

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = turns[this.turn++];
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    /// <summary>A client that never streams; used when a fake loop replaces real sampling.</summary>
    private sealed class InertClient : ILlmClient, IDisposable
    {
        public string ProviderId => ClaudeAiProvider.Id;

        public bool Disposed { get; private set; }

        public void Dispose() => this.Disposed = true;

        public IAsyncEnumerable<AssistantStreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("InertClient must not be streamed.");
    }

    /// <summary>Returns a fixed client (or null to simulate an unsupported provider).</summary>
    private sealed class FakeClientFactory(ILlmClient? client) : ILlmClientFactory
    {
        public int Calls { get; private set; }

        public ILlmClient? Create(
            string providerId,
            CredentialManager credentials,
            ClientFingerprint fingerprint,
            HttpClient httpClient,
            Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
            LlmHttpTimeoutConfig? timeoutConfig = null,
            IStreamProgressSink? progressSink = null)
        {
            this.Calls++;
            return client;
        }
    }

    /// <summary>Records each spec it is handed and returns a supplied loop.</summary>
    private sealed class RecordingLoopFactory(IAgentLoop loop) : IAgentLoopFactory
    {
        public List<AgentLoopSpec> Specs { get; } = [];

        public IAgentLoop Create(AgentLoopSpec spec)
        {
            this.Specs.Add(spec);
            return loop;
        }
    }

    /// <summary>A loop whose body is a supplied delegate; captures the history it is run against.</summary>
    private sealed class ProgrammableLoop(Func<List<ChatMessage>, IAgentSink, CancellationToken, Task> body) : IAgentLoop
    {
        public List<ChatMessage>? SeenHistory { get; private set; }

        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default)
        {
            this.SeenHistory = history;
            return body(history, sink, cancellationToken);
        }
    }

    /// <summary>A read-only tool that records the ToolContext it was invoked with.</summary>
    private sealed class ProbeTool : ITool
    {
        public List<(string? TaskId, int Depth, List<string> ToolNames)> Observations { get; } = [];

        public string Name => "probe";

        public string Description => "records context";

        public string InputSchemaJson => "{}";

        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            this.Observations.Add((
                context.CurrentTaskId,
                context.CurrentDepth,
                (context.AllTools ?? []).Select(t => t.Name).ToList()));
            return Task.FromResult(new ToolResult("ok"));
        }
    }

    /// <summary>A trivial extra (MCP-style) marker tool.</summary>
    private sealed class FakeMcpTool : ITool
    {
        public const string ToolName = "fake_mcp";

        public string Name => ToolName;

        public string Description => "mcp";

        public string InputSchemaJson => "{}";

        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult(string.Empty));
    }

    /// <summary>A mutating tool used to exercise permission decisions.</summary>
    private sealed class WriteMarkerTool : ITool
    {
        public string Name => "write_marker";

        public string Description => "writes";

        public string InputSchemaJson => "{}";

        public bool IsReadOnly => false;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult(string.Empty));
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    // ---- Builders --------------------------------------------------------------------------

    private static LspServerManager StubLspManager()
    {
        var configs = new Dictionary<string, LspServerConfig>
        {
            ["python"] = new LspServerConfig("pylsp", [], new Dictionary<string, string>(), null, null, null),
        };
        return new LspServerManager(
            configs,
            (name, cfg) => throw new InvalidOperationException("LSP instance factory not expected during BuildScheduledSpec"));
    }

    private TaskManager NewTaskManager() => new(sessionId: "sched", logRoot: null);

    private TurnPipelineBuilder NewBuilder(
        TaskManager tasks,
        LspServerManager? lspManager = null,
        LspDiagnosticRegistry? lspDiagnostics = null,
        ToolSearchCoordinator? toolSearch = null)
    {
        return new TurnPipelineBuilder(
            new TodoStore(),
            new ScheduledTaskStore(),
            tasks,
            lspManager,
            lspDiagnostics,
            toolSearch,
            NullLoggerFactory.Instance,
            (_, _, _) => Task.CompletedTask,
            () => null);
    }

    private SessionOptions Options(
        PermissionMode mode = PermissionMode.Default,
        PermissionModeState? state = null,
        string? effort = null,
        string? outputStyle = null,
        string model = "claude-sonnet-4-6",
        IReadOnlyList<ITool>? extraTools = null,
        string providerId = ClaudeAiProvider.Id)
    {
        return new SessionOptions
        {
            ProviderId = providerId,
            Model = model,
            WorkingDirectory = this.root,
            PermissionMode = mode,
            PermissionModeState = state,
            Effort = effort,
            OutputStyle = outputStyle,
            ExtraTools = extraTools ?? [],
        };
    }

    private ScheduledAgentHost NewHost(
        Func<SessionOptions> currentOptions,
        ILlmClientFactory clientFactory,
        IAgentLoopFactory loopFactory,
        TurnPipelineBuilder pipeline,
        HttpClient http)
    {
        return new ScheduledAgentHost(
            currentOptions,
            clientFactory,
            loopFactory,
            SignedInClaude(),
            new ClientFingerprint(),
            http,
            NullLoggerFactory.Instance,
            pipeline);
    }

    // ---- BuildScheduledSpec: identity, isolation, tools ------------------------------------

    [Fact]
    public void BuildScheduledSpec_sets_current_task_id_and_depth()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();
        var client = new ScriptedClient();

        var spec = builder.BuildScheduledSpec(this.Options(), client, CodaSettings.Empty, "task-0007", depth: 1);

        Assert.Equal("task-0007", spec.CurrentTaskId);
        Assert.Equal(1, spec.CurrentDepth);
    }

    [Fact]
    public void BuildScheduledSpec_nulls_the_isolated_session_wiring()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks, lspManager: StubLspManager(), lspDiagnostics: new LspDiagnosticRegistry());
        var client = new ScriptedClient();

        var spec = builder.BuildScheduledSpec(this.Options(), client, CodaSettings.Empty, "task-1", depth: 1);

        Assert.Null(spec.Todos);
        Assert.Null(spec.Schedules);
        Assert.Null(spec.ScheduleRuntime);
        Assert.Null(spec.Goal);
        Assert.Null(spec.CompactAsync);
        Assert.Null(spec.PersistTurnAsync);
        Assert.Null(spec.Gate);
        Assert.Null(spec.Steering);
        // Shared collaborators still threaded.
        Assert.Same(tasks, spec.Tasks);
        Assert.NotNull(spec.Lsp);
        Assert.NotNull(spec.Subagents);
    }

    [Fact]
    public void BuildScheduledSpec_includes_mcp_task_and_lsp_but_strips_schedule_tools()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks, lspManager: StubLspManager(), lspDiagnostics: new LspDiagnosticRegistry());
        var client = new ScriptedClient();
        var options = this.Options(extraTools: [new FakeMcpTool()]);

        var spec = builder.BuildScheduledSpec(options, client, CodaSettings.Empty, "task-1", depth: 1);

        var names = spec.Tools.All.Select(t => t.Name).ToHashSet();
        Assert.Contains("read_file", names);
        Assert.Contains("task", names);            // TaskTool present
        Assert.Contains("task_start", names);      // task lifecycle preserved
        Assert.Contains("task_list", names);
        Assert.Contains("lsp", names);             // LSP configured
        Assert.Contains(FakeMcpTool.ToolName, names);
        Assert.DoesNotContain("schedule_create", names);
        Assert.DoesNotContain("schedule_list", names);
        Assert.DoesNotContain("schedule_delete", names);
    }

    [Fact]
    public void BuildScheduledSpec_omits_lsp_tool_when_no_manager()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        var client = new ScriptedClient();

        var spec = builder.BuildScheduledSpec(this.Options(), client, CodaSettings.Empty, "task-1", depth: 1);

        Assert.DoesNotContain("lsp", spec.Tools.All.Select(t => t.Name));
        Assert.Null(spec.Lsp);
    }

    [Fact]
    public void BuildScheduledSpec_adds_tool_search_when_configured()
    {
        var tasks = this.NewTaskManager();
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.TstAuto);
        var builder = this.NewBuilder(tasks, toolSearch: coordinator);
        var client = new ScriptedClient();

        var spec = builder.BuildScheduledSpec(this.Options(), client, CodaSettings.Empty, "task-1", depth: 1);

        Assert.Contains("tool_search", spec.Tools.All.Select(t => t.Name));
        Assert.Same(coordinator, spec.ToolSearch);
    }

    [Fact]
    public void BuildScheduledSpec_threads_user_hooks_but_no_session_memory_hooks()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        var client = new ScriptedClient();
        var settings = new CodaSettings([], [], [new UserHook("PreToolUse", "echo hi", null)]);

        // Even with session memory requested on the options, isolated scheduled runs do not
        // carry the SessionMemory post-sampling hook bus; user-configured hooks still apply.
        var options = this.Options() with { EnableSessionMemory = true };
        var spec = builder.BuildScheduledSpec(options, client, settings, "task-1", depth: 1);

        Assert.Null(spec.Hooks);
        Assert.NotNull(spec.UserHooks);
    }

    [Fact]
    public void BuildScheduledSpec_threads_prompt_services()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        var client = new ScriptedClient();
        var question = new StubQuestionPrompt();
        var approver = new StubPlanApprover();
        var options = this.Options() with { UserQuestionPrompt = question, PlanApprover = approver };

        var spec = builder.BuildScheduledSpec(options, client, CodaSettings.Empty, "task-1", depth: 1);

        Assert.Same(question, spec.UserQuestion);
        Assert.Same(approver, spec.PlanApprover);
    }

    private sealed class StubQuestionPrompt : IUserQuestionPrompt
    {
        public Task<string> AskAsync(string question, IReadOnlyList<string> options, bool multiSelect, CancellationToken cancellationToken = default)
            => Task.FromResult(options.Count > 0 ? options[0] : string.Empty);
    }

    private sealed class StubPlanApprover : IPlanApprover
    {
        public Task<bool> ApproveAsync(string plan, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    [Fact]
    public async Task BuildScheduledSpec_uses_live_permission_state_at_decision_time()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        var client = new ScriptedClient();
        var state = new PermissionModeState(PermissionMode.Default);

        var spec = builder.BuildScheduledSpec(this.Options(state: state), client, CodaSettings.Empty, "task-1", depth: 1);
        var write = new WriteMarkerTool();

        // Default mode: a mutating tool asks; headless (no inner prompt) → deny.
        Assert.False(await spec.Permissions.RequestAsync(write, "{}"));

        // Flip the shared state live; the SAME prompt now allows.
        state.Mode = PermissionMode.BypassPermissions;
        Assert.True(await spec.Permissions.RequestAsync(write, "{}"));
    }

    // ---- ScheduledAgentHost: isolated history ---------------------------------------------

    [Fact]
    public async Task RunScheduledAsync_runs_only_the_prompt_as_local_history()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();
        var loop = new ProgrammableLoop((_, sink, _) =>
        {
            sink.OnAssistantText("hi there");
            sink.OnAssistantTextComplete();
            return Task.CompletedTask;
        });
        var host = this.NewHost(() => this.Options(), new FakeClientFactory(new InertClient()), new RecordingLoopFactory(loop), builder, http);

        var text = await host.RunScheduledAsync("do the work", new NullSink(), new SteeringInbox(), "task-1", 1, CancellationToken.None);

        Assert.Equal("hi there", text);
        Assert.NotNull(loop.SeenHistory);
        Assert.Single(loop.SeenHistory!);
        var block = Assert.IsType<TextBlock>(loop.SeenHistory![0].Content[0]);
        Assert.Equal("do the work", block.Text);
    }

    [Fact]
    public async Task RunScheduledAsync_history_stays_isolated_on_failure()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();
        var loop = new ProgrammableLoop((_, _, _) => throw new InvalidOperationException("boom"));
        var host = this.NewHost(() => this.Options(), new FakeClientFactory(new InertClient()), new RecordingLoopFactory(loop), builder, http);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.RunScheduledAsync("prompt", new NullSink(), new SteeringInbox(), "task-1", 1, CancellationToken.None));

        Assert.NotNull(loop.SeenHistory);
        Assert.Single(loop.SeenHistory!);
    }

    [Fact]
    public async Task RunScheduledAsync_propagates_cancellation()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();
        var loop = new ProgrammableLoop((_, _, ct) => throw new OperationCanceledException(ct));
        var host = this.NewHost(() => this.Options(), new FakeClientFactory(new InertClient()), new RecordingLoopFactory(loop), builder, http);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.RunScheduledAsync("prompt", new NullSink(), new SteeringInbox(), "task-1", 1, cts.Token));

        Assert.Single(loop.SeenHistory!);
    }

    [Fact]
    public async Task RunScheduledAsync_returns_placeholder_when_no_text()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();
        var loop = new ProgrammableLoop((_, _, _) => Task.CompletedTask);
        var host = this.NewHost(() => this.Options(), new FakeClientFactory(new InertClient()), new RecordingLoopFactory(loop), builder, http);

        var text = await host.RunScheduledAsync("prompt", new NullSink(), new SteeringInbox(), "task-1", 1, CancellationToken.None);

        Assert.Equal("(scheduled task completed)", text);
    }

    [Fact]
    public async Task RunScheduledAsync_throws_for_unsupported_provider()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();
        var loop = new ProgrammableLoop((_, _, _) => Task.CompletedTask);
        var host = this.NewHost(
            () => this.Options(providerId: "nope"),
            new FakeClientFactory(null),
            new RecordingLoopFactory(loop),
            builder,
            http);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.RunScheduledAsync("prompt", new NullSink(), new SteeringInbox(), "task-1", 1, CancellationToken.None));
    }

    [Fact]
    public async Task RunScheduledAsync_applies_the_supplied_steering_inbox()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();
        var loop = new ProgrammableLoop((_, _, _) => Task.CompletedTask);
        var factory = new RecordingLoopFactory(loop);
        var host = this.NewHost(() => this.Options(), new FakeClientFactory(new InertClient()), factory, builder, http);
        var steering = new SteeringInbox();

        await host.RunScheduledAsync("prompt", new NullSink(), steering, "task-1", 1, CancellationToken.None);

        Assert.Same(steering, factory.Specs[0].Steering);
    }

    // ---- ScheduledAgentHost: per-firing live options --------------------------------------

    [Fact]
    public async Task RunScheduledAsync_reads_current_options_each_firing()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();
        var loop = new ProgrammableLoop((_, _, _) => Task.CompletedTask);
        var factory = new RecordingLoopFactory(loop);

        var current = this.Options(model: "claude-sonnet-4-6", effort: "low", outputStyle: null, extraTools: []);
        var host = this.NewHost(() => current, new FakeClientFactory(new InertClient()), factory, builder, http);

        await host.RunScheduledAsync("p", new NullSink(), new SteeringInbox(), "task-1", 1, CancellationToken.None);

        // Mutate every live-observed knob before the second firing.
        current = this.Options(model: "claude-opus-4-1", effort: "high", outputStyle: "concise", extraTools: [new FakeMcpTool()]);

        await host.RunScheduledAsync("p", new NullSink(), new SteeringInbox(), "task-2", 1, CancellationToken.None);

        var first = factory.Specs[0];
        var second = factory.Specs[1];

        Assert.Equal("claude-sonnet-4-6", first.Options.Model);
        Assert.Equal("claude-opus-4-1", second.Options.Model);
        Assert.Equal("low", first.Options.Effort);
        Assert.Equal("high", second.Options.Effort);
        Assert.NotEqual(first.Options.SystemPrompt, second.Options.SystemPrompt); // output style changed
        Assert.DoesNotContain(FakeMcpTool.ToolName, first.Tools.All.Select(t => t.Name));
        Assert.Contains(FakeMcpTool.ToolName, second.Tools.All.Select(t => t.Name));
    }

    [Fact]
    public async Task RunScheduledAsync_observes_live_permission_state_between_firings()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();
        var loop = new ProgrammableLoop((_, _, _) => Task.CompletedTask);
        var factory = new RecordingLoopFactory(loop);
        var state = new PermissionModeState(PermissionMode.Default);
        var host = this.NewHost(() => this.Options(state: state), new FakeClientFactory(new InertClient()), factory, builder, http);
        var write = new WriteMarkerTool();

        await host.RunScheduledAsync("p", new NullSink(), new SteeringInbox(), "task-1", 1, CancellationToken.None);
        Assert.False(await factory.Specs[0].Permissions.RequestAsync(write, "{}"));

        state.Mode = PermissionMode.BypassPermissions;
        await host.RunScheduledAsync("p", new NullSink(), new SteeringInbox(), "task-2", 1, CancellationToken.None);
        Assert.True(await factory.Specs[1].Permissions.RequestAsync(write, "{}"));
    }

    // ---- ScheduledAgentHost: client disposal ----------------------------------------------

    [Fact]
    public async Task RunScheduledAsync_disposes_client_on_success()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();
        var client = new InertClient();
        var loop = new ProgrammableLoop((_, _, _) => Task.CompletedTask);
        var host = this.NewHost(() => this.Options(), new FakeClientFactory(client), new RecordingLoopFactory(loop), builder, http);

        await host.RunScheduledAsync("p", new NullSink(), new SteeringInbox(), "task-1", 1, CancellationToken.None);

        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task RunScheduledAsync_disposes_client_on_failure()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();
        var client = new InertClient();
        var loop = new ProgrammableLoop((_, _, _) => throw new InvalidOperationException("boom"));
        var host = this.NewHost(() => this.Options(), new FakeClientFactory(client), new RecordingLoopFactory(loop), builder, http);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.RunScheduledAsync("p", new NullSink(), new SteeringInbox(), "task-1", 1, CancellationToken.None));

        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task RunScheduledAsync_disposes_client_on_cancel()
    {
        var tasks = this.NewTaskManager();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();
        var client = new InertClient();
        var loop = new ProgrammableLoop((_, _, ct) => throw new OperationCanceledException(ct));
        var host = this.NewHost(() => this.Options(), new FakeClientFactory(client), new RecordingLoopFactory(loop), builder, http);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.RunScheduledAsync("p", new NullSink(), new SteeringInbox(), "task-1", 1, cts.Token));

        Assert.True(client.Disposed);
    }

    // ---- Integration: ToolContext identity/depth via a real loop --------------------------

    /// <summary>Drives the host through the real StartScheduledBackground registration so the
    /// scheduled root is a registered depth-1 task (its id/depth flow to the ToolContext), then
    /// awaits the terminal transition.</summary>
    private static async Task<TaskSnapshot> RunScheduledToTerminalAsync(TaskManager tasks, IScheduledAgentHost host)
    {
        var done = new TaskCompletionSource<TaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        tasks.StartScheduledBackground(host, "go", "scheduled", snap => done.TrySetResult(snap));
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Scheduled_root_tool_context_carries_task_id_and_depth()
    {
        var tasks = this.NewTaskManager();
        var probe = new ProbeTool();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();

        var client = new ScriptedClient(
            [AssistantStreamEvent.Tool(new ToolUseBlock("t1", "probe", "{}")), AssistantStreamEvent.Finished("tool_use")],
            [AssistantStreamEvent.Delta("done"), AssistantStreamEvent.Finished("end_turn")]);

        var host = this.NewHost(
            () => this.Options(mode: PermissionMode.BypassPermissions, extraTools: [probe]),
            new FakeClientFactory(client),
            new DefaultAgentLoopFactory(),
            builder,
            http);

        var snap = await RunScheduledToTerminalAsync(tasks, host);
        Assert.Equal(TaskRunStatus.Completed, snap.Status);

        var obs = Assert.Single(probe.Observations);
        Assert.Equal(snap.Id, obs.TaskId); // the registered scheduled root's id
        Assert.Equal(1, obs.Depth);
        Assert.Contains("task", obs.ToolNames);
        Assert.Contains("task_start", obs.ToolNames);
        Assert.DoesNotContain("schedule_create", obs.ToolNames);
    }

    [Fact]
    public async Task Scheduled_root_creates_depth2_child_that_cannot_create_depth3()
    {
        var tasks = this.NewTaskManager();
        var probe = new ProbeTool();
        var builder = this.NewBuilder(tasks);
        using var http = new HttpClient();

        // root turn1: call task -> spawn a child.
        // child turn1: call probe (records depth 2).
        // child turn2: finish.
        // root turn2: finish.
        var client = new ScriptedClient(
            [AssistantStreamEvent.Tool(new ToolUseBlock("r1", "task", """{"description":"child","prompt":"work"}""")), AssistantStreamEvent.Finished("tool_use")],
            [AssistantStreamEvent.Tool(new ToolUseBlock("c1", "probe", "{}")), AssistantStreamEvent.Finished("tool_use")],
            [AssistantStreamEvent.Delta("child done"), AssistantStreamEvent.Finished("end_turn")],
            [AssistantStreamEvent.Delta("root done"), AssistantStreamEvent.Finished("end_turn")]);

        var host = this.NewHost(
            () => this.Options(mode: PermissionMode.BypassPermissions, extraTools: [probe]),
            new FakeClientFactory(client),
            new DefaultAgentLoopFactory(),
            builder,
            http);

        var snap = await RunScheduledToTerminalAsync(tasks, host);
        Assert.Equal(TaskRunStatus.Completed, snap.Status);

        var obs = Assert.Single(probe.Observations);
        Assert.Equal(2, obs.Depth); // depth-2 child ran
        // A depth-2 child cannot create depth-3: it has no task-creation/management tools...
        Assert.DoesNotContain("task", obs.ToolNames);
        Assert.DoesNotContain("task_start", obs.ToolNames);
        // ...and never any schedule_* tools.
        Assert.DoesNotContain("schedule_create", obs.ToolNames);
        Assert.DoesNotContain("schedule_list", obs.ToolNames);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
