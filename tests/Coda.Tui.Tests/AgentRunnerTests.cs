using System.Runtime.CompilerServices;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Sdk;
using Coda.Tui;
using Coda.Tui.Agent;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Tests;

/// <summary>
/// Integration coverage for <see cref="AgentRunner"/>: the turn lifecycle events it publishes
/// (prompt → start → sink events → runtime/context/git/cost/metadata → completed), interruption
/// semantics, active-turn state, and shared/owned disposal — all driven against a fake session
/// (fake client + fake agent loop) so no network is required.
/// </summary>
public sealed class AgentRunnerTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_agentrunner_").FullName;
    private readonly HttpClient http = new(new BlockingHandler());

    public void Dispose()
    {
        this.http.Dispose();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Successful_turn_publishes_prompt_start_sink_and_post_turn_events_in_order()
    {
        var events = new RecordingUiEvents();
        var context = this.BuildContext(events, out _);
        context.Session.Effort = "high";
        context.Session.Model = "claude-sonnet-4-6";
        using var runner = NewRunner(new ScriptedLoop());

        await runner.RunAsync(context, "hi", CancellationToken.None);

        var recorded = events.Events;

        // Prompt + turn start precede any streamed sink event.
        Assert.IsType<UserPromptSubmittedEvent>(recorded[0]);
        Assert.Equal("hi", ((UserPromptSubmittedEvent)recorded[0]).Text);
        Assert.IsType<TurnStartedEvent>(recorded[1]);
        Assert.Equal("hi", ((TurnStartedEvent)recorded[1]).Prompt);

        var firstSink = recorded.ToList().FindIndex(e => e is AssistantTextDeltaEvent);
        Assert.True(firstSink > 1, "sink events must follow the turn-start event");

        // Post-turn runtime refresh events all come after the sink events.
        var runtime = IndexOf<SessionRuntimeChangedEvent>(recorded);
        var contextChanged = IndexOf<ContextChangedEvent>(recorded);
        var git = IndexOf<GitChangedEvent>(recorded);
        var cost = IndexOf<CostEstimateChangedEvent>(recorded);
        var metadata = IndexOf<SessionMetadataChangedEvent>(recorded);
        var completed = IndexOf<TurnCompletedEvent>(recorded);

        Assert.True(runtime > firstSink);
        Assert.True(contextChanged > firstSink);
        Assert.True(git > firstSink);
        Assert.True(cost > firstSink);
        Assert.True(metadata > firstSink);

        // Turn completion is the final event and reports success.
        Assert.Equal(recorded.Count - 1, completed);
        Assert.True(((TurnCompletedEvent)recorded[completed]).Success);

        // Payloads reflect the caches / session.
        var ctxEvent = (ContextChangedEvent)recorded[contextChanged];
        Assert.Equal(1234, ctxEvent.Context.UsedTokens);
        Assert.Equal(200000, ctxEvent.Context.MaxTokens);
        Assert.True(ctxEvent.Context.IsExact);

        var gitEvent = (GitChangedEvent)recorded[git];
        Assert.Equal("main", gitEvent.Git.Branch);
        Assert.True(gitEvent.Git.Dirty);

        var metadataEvent = (SessionMetadataChangedEvent)recorded[metadata];
        Assert.Equal("claude-sonnet-4-6", metadataEvent.Model);
        Assert.Equal("high", metadataEvent.EffectiveEffort);
        Assert.Equal("high", metadataEvent.RequestedEffort);

        // Usage propagated for /cost.
        Assert.Equal(new TokenUsage(120, 30), context.Session.SessionUsage);
        Assert.DoesNotContain(recorded, e => e is TurnInterruptedEvent);
    }

    [Fact]
    public async Task Interrupted_turn_publishes_TurnInterrupted_and_clears_active_state()
    {
        var events = new RecordingUiEvents();
        var context = this.BuildContext(events, out _);
        var loop = new BlockingLoop();
        using var runner = NewRunner(loop);

        var turn = runner.RunAsync(context, "long", CancellationToken.None);
        await loop.Started;

        Assert.True(runner.HasActiveTurn);
        Assert.True(runner.TryInterruptActiveTurn());

        await turn;

        Assert.False(runner.HasActiveTurn);
        Assert.Contains(events.Events, e => e is TurnInterruptedEvent);
        Assert.DoesNotContain(events.Events, e => e is TurnCompletedEvent);
        // Post-turn refresh events must not fire on an interrupted turn.
        Assert.DoesNotContain(events.Events, e => e is SessionRuntimeChangedEvent);
    }

    [Fact]
    public void TryInterruptActiveTurn_returns_false_when_idle()
    {
        using var runner = NewRunner(new ScriptedLoop());
        Assert.False(runner.HasActiveTurn);
        Assert.False(runner.TryInterruptActiveTurn());
    }

    [Fact]
    public async Task Interrupting_only_cancels_the_turn_not_the_caller_token()
    {
        var events = new RecordingUiEvents();
        var context = this.BuildContext(events, out _);
        var loop = new BlockingLoop();
        using var runner = NewRunner(loop);
        using var callerCts = new CancellationTokenSource();

        var turn = runner.RunAsync(context, "long", callerCts.Token);
        await loop.Started;

        Assert.True(runner.TryInterruptActiveTurn());
        await turn;

        // The interrupt cancels only the per-turn token; the caller's token is untouched.
        Assert.False(callerCts.IsCancellationRequested);
    }

    [Fact]
    public async Task GetRuntimeSnapshot_is_null_before_a_turn_and_a_copy_after()
    {
        var events = new RecordingUiEvents();
        var context = this.BuildContext(events, out _);
        using var runner = NewRunner(new ScriptedLoop());

        Assert.Null(runner.GetRuntimeSnapshot());

        await runner.RunAsync(context, "hi", CancellationToken.None);

        var snapshot = runner.GetRuntimeSnapshot();
        Assert.NotNull(snapshot);
        Assert.NotSame(runner.GetRuntimeSnapshot(), snapshot); // each call returns a fresh copy
    }

    [Fact]
    public async Task TuiApp_does_not_dispose_a_shared_runner()
    {
        var events = new RecordingUiEvents();
        var context = this.BuildContext(events, out _);
        var shared = NewRunner(new ScriptedLoop());

        using (var app = new TuiApp(context, agentRunner: shared))
        {
            Assert.Same(shared, app.Runner);
        }

        Assert.False(shared.IsDisposed);
        shared.Dispose();
    }

    [Fact]
    public void TuiApp_disposes_its_own_default_runner()
    {
        var events = new RecordingUiEvents();
        var context = this.BuildContext(events, out _);
        var app = new TuiApp(context);
        var owned = app.Runner;

        app.Dispose();

        Assert.True(owned.IsDisposed);
    }

    [Fact]
    public void SessionState_PermissionMode_delegates_to_the_stable_shared_state()
    {
        var session = new SessionState("claude-ai", this.tempDir);

        // The getter reads the shared state; the setter writes through to it.
        Assert.Equal(PermissionMode.Default, session.PermissionMode);
        Assert.Equal(PermissionMode.Default, session.PermissionModes.Mode);

        session.PermissionMode = PermissionMode.BypassPermissions;
        Assert.Equal(PermissionMode.BypassPermissions, session.PermissionModes.Mode);

        // A change straight to the shared state is visible through the property (same instance).
        session.PermissionModes.Mode = PermissionMode.Plan;
        Assert.Equal(PermissionMode.Plan, session.PermissionMode);
    }

    [Fact]
    public async Task BuildOptions_passes_the_exact_session_permission_state_reference()
    {
        var events = new RecordingUiEvents();
        var context = this.BuildContext(events, out var session);
        SessionOptions? captured = null;
        using var runner = new AgentRunner(
            extraToolsProvider: null,
            sessionFactory: (ctx, options) =>
            {
                captured = options;
                return new CodaSession(
                    ctx.Credentials,
                    options,
                    httpClient: this.http,
                    history: ctx.Session.History,
                    sessionId: ctx.Session.SessionId,
                    llmClientFactory: new StubClientFactory(new StubClient()),
                    agentLoopFactory: new SingleLoopFactory(new ScriptedLoop()));
            });

        await runner.RunAsync(context, "hi", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Same(session.PermissionModes, captured!.PermissionModeState);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static int IndexOf<T>(IReadOnlyList<UiEvent> events)
        where T : UiEvent
    {
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is T)
            {
                return i;
            }
        }

        return -1;
    }

    private AgentRunner NewRunner(IAgentLoop loop) => new(
        extraToolsProvider: null,
        sessionFactory: (context, options) => new CodaSession(
            context.Credentials,
            options,
            httpClient: this.http,
            history: context.Session.History,
            sessionId: context.Session.SessionId,
            llmClientFactory: new StubClientFactory(new StubClient()),
            agentLoopFactory: new SingleLoopFactory(loop)));

    private CommandContext BuildContext(IUiEventPublisher events, out SessionState session)
    {
        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude });
        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };
        session = new SessionState("claude-ai", this.tempDir);
        var registry = new SlashCommandRegistry(Array.Empty<ISlashCommand>());

        var contextCache = new ContextSnapshotCache(_ => Task.FromResult(new ContextReport
        {
            Model = "claude-sonnet-4-6",
            MaxTokens = 200000,
            Categories = [],
            UsedTokens = 1234,
            IsExact = true,
            MessageCount = 2,
        }));
        var gitCache = new GitStatusCache((_, _) => Task.FromResult(new GitStatus("main", true)));

        return new CommandContext(
            new Spectre.Console.Testing.TestConsole(),
            credentials,
            session,
            providers,
            registry,
            events: events,
            contextSnapshots: contextCache,
            gitStatus: gitCache);
    }

    /// <summary>Fake loop that streams a short canned reply, usage, and stop reason.</summary>
    private sealed class ScriptedLoop : IAgentLoop
    {
        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default)
        {
            sink.OnAssistantText("hello from model");
            sink.OnAssistantTextComplete();
            sink.OnUsage(new TokenUsage(120, 30));
            sink.OnStopReason("end_turn");
            return Task.CompletedTask;
        }
    }

    /// <summary>Fake loop that blocks until cancelled, so a turn can be interrupted mid-flight.</summary>
    private sealed class BlockingLoop : IAgentLoop
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => this.started.Task;

        public GoalStatus? LastGoalStatus => null;

        public async Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default)
        {
            this.started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class SingleLoopFactory(IAgentLoop loop) : IAgentLoopFactory
    {
        public IAgentLoop Create(AgentLoopSpec spec) => loop;
    }

    private sealed class StubClientFactory(ILlmClient client) : ILlmClientFactory
    {
        public ILlmClient? Create(
            string providerId,
            CredentialManager credentials,
            ClientFingerprint fingerprint,
            HttpClient httpClient,
            ILoggerFactory? loggerFactory = null,
            LlmHttpTimeoutConfig? timeoutConfig = null,
            IStreamProgressSink? progressSink = null) => client;
    }

    private sealed class StubClient : ILlmClient
    {
        public string ProviderId => ClaudeAiProvider.Id;

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network call expected in AgentRunner tests.");
    }
}
