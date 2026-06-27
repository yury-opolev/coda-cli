using System.Runtime.CompilerServices;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Microsoft.Extensions.Logging;
using static Engine.Tests.TestSupport.CredentialFixtures;

namespace Engine.Tests.Sdk;

/// <summary>
/// Unit-covers <see cref="CodaSession.RunAsync(IReadOnlyList{ContentBlock}, IAgentSink?, System.Threading.CancellationToken)"/>
/// orchestration edge behaviors by driving it against FAKE collaborators (a fake
/// <see cref="IAgentLoopFactory"/>/<see cref="IAgentLoop"/> and a fake
/// <see cref="ILlmClientFactory"/>/<see cref="ILlmClient"/>) — no real provider or real
/// agent loop. Each test asserts a concrete observable effect (history counts, RunResult
/// fields, goal status, compaction side effects), pinning the session's current contract.
/// </summary>
public sealed class CodaSessionRunAsyncTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_session_runasync_").FullName;

    private SessionOptions Options() => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = PermissionMode.BypassPermissions,
    };

    /// <summary>
    /// Fake provider client. Records how many times <see cref="StreamAsync"/> is called (only
    /// compaction calls it here, since the loop is faked) and emits canned text so a forked
    /// compaction summary is non-empty.
    /// </summary>
    private sealed class FakeLlmClient : ILlmClient
    {
        public int StreamCalls { get; private set; }

        public string ProviderId => ClaudeAiProvider.Id;

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.StreamCalls++;
            await Task.Yield();
            yield return AssistantStreamEvent.Delta("compacted summary");
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }

    /// <summary>Factory that returns a fixed client (or null), recording the providers requested.</summary>
    private sealed class StubClientFactory : ILlmClientFactory
    {
        private readonly ILlmClient? client;

        public StubClientFactory(ILlmClient? client) => this.client = client;

        public int CreateCalls { get; private set; }

        public ILlmClient? Create(
            string providerId,
            CredentialManager credentials,
            ClientFingerprint fingerprint,
            HttpClient httpClient,
            ILoggerFactory? loggerFactory = null,
            LlmHttpTimeoutConfig? timeoutConfig = null,
            IStreamProgressSink? progressSink = null)
        {
            this.CreateCalls++;
            return this.client;
        }
    }

    /// <summary>
    /// Configurable fake loop: optionally throws (cancellation or a generic failure), exposes a
    /// canned <see cref="LastGoalStatus"/>, emits canned output to the sink, and records the
    /// history reference it was driven over.
    /// </summary>
    private sealed class ConfigurableLoop : IAgentLoop
    {
        private readonly Exception? toThrow;
        private readonly string? cannedText;

        public ConfigurableLoop(GoalStatus? goal = null, Exception? toThrow = null, string? cannedText = "loop reply")
        {
            this.LastGoalStatus = goal;
            this.toThrow = toThrow;
            this.cannedText = cannedText;
        }

        public GoalStatus? LastGoalStatus { get; }

        public int RunCalls { get; private set; }

        public List<ChatMessage>? DrivenHistory { get; private set; }

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default)
        {
            this.RunCalls++;
            this.DrivenHistory = history;
            if (this.toThrow is not null)
            {
                throw this.toThrow;
            }

            if (this.cannedText is not null)
            {
                sink.OnAssistantText(this.cannedText);
                sink.OnAssistantTextComplete();
            }

            sink.OnUsage(new TokenUsage(7, 11));
            sink.OnStopReason("end_turn");
            return Task.CompletedTask;
        }
    }

    /// <summary>Returns a fixed loop, recording the spec it was handed and whether it was called.</summary>
    private sealed class StubLoopFactory : IAgentLoopFactory
    {
        private readonly IAgentLoop loop;

        public StubLoopFactory(IAgentLoop loop) => this.loop = loop;

        public int CreateCalls { get; private set; }

        public IAgentLoop Create(AgentLoopSpec spec)
        {
            this.CreateCalls++;
            return this.loop;
        }
    }

    private CodaSession NewSession(
        ILlmClient? client,
        IAgentLoop loop,
        IAgentLoopFactory loopFactory,
        List<ChatMessage>? history = null,
        SessionOptions? options = null)
    {
        return new CodaSession(
            SignedInClaude(),
            options ?? this.Options(),
            httpClient: new HttpClient(new ThrowingHandler()),
            history: history,
            llmClientFactory: new StubClientFactory(client),
            agentLoopFactory: loopFactory);
    }

    /// <summary>Asserts no real HTTP ever leaks out: every collaborator is faked.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No real HTTP should be issued: all collaborators are faked.");
    }

    [Fact]
    public async Task RunAsync_returns_no_client_failure_without_building_a_loop()
    {
        // ILlmClientFactory.Create returns null → RunAsync must short-circuit BEFORE the loop.
        var loop = new ConfigurableLoop();
        var loopFactory = new StubLoopFactory(loop);
        using var session = this.NewSession(client: null, loop, loopFactory);

        var result = await session.RunAsync("hi");

        Assert.False(result.Success);
        Assert.Equal($"No chat client for provider '{ClaudeAiProvider.Id}'.", result.Error);
        Assert.Equal(0, loopFactory.CreateCalls);
        Assert.Equal(0, loop.RunCalls);
        // The user message is never added when there is no client.
        Assert.Empty(session.History);
    }

    [Fact]
    public async Task RunAsync_rolls_history_back_on_loop_failure()
    {
        // A non-cancellation throw must roll history back to the pre-turn snapshot.
        var loop = new ConfigurableLoop(toThrow: new InvalidOperationException("loop boom"));
        var loopFactory = new StubLoopFactory(loop);
        var seeded = new List<ChatMessage> { ChatMessage.UserText("earlier") };
        using var session = this.NewSession(new FakeLlmClient(), loop, loopFactory, history: seeded);
        var before = session.History.Count;

        var result = await session.RunAsync("hi");

        Assert.False(result.Success);
        Assert.Equal("loop boom", result.Error);
        Assert.Null(result.StopReason);
        Assert.Equal(1, loop.RunCalls);
        // The user message added at turn start is removed; history returns to its pre-turn count.
        Assert.Equal(before, session.History.Count);
    }

    [Fact]
    public async Task RunAsync_propagates_goal_status_on_success()
    {
        var goal = new GoalStatus(GoalOutcome.Met, Remaining: null, Continuations: 3, Elapsed: TimeSpan.FromSeconds(2), Escalated: true, ExtensionUsed: false);
        var loop = new ConfigurableLoop(goal: goal);
        using var session = this.NewSession(new FakeLlmClient(), loop, new StubLoopFactory(loop));

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        Assert.Equal(goal, result.Goal);
        Assert.Equal(GoalOutcome.Met, result.Goal!.Outcome);
    }

    [Fact]
    public async Task RunAsync_propagates_goal_status_on_failure()
    {
        var goal = new GoalStatus(GoalOutcome.Unmet, Remaining: "do more", Continuations: 1, Elapsed: TimeSpan.FromSeconds(1), Escalated: false, ExtensionUsed: true);
        var loop = new ConfigurableLoop(goal: goal, toThrow: new InvalidOperationException("boom"));
        using var session = this.NewSession(new FakeLlmClient(), loop, new StubLoopFactory(loop));

        var result = await session.RunAsync("hi");

        Assert.False(result.Success);
        // The failure result still carries the loop's last goal status.
        Assert.Equal(goal, result.Goal);
    }

    [Fact]
    public async Task RunAsync_returns_canceled_result_and_rolls_back_on_cancellation()
    {
        // PINNED CONTRACT: an OperationCanceledException from the loop does NOT rethrow — RunAsync
        // catches it, rolls history back, and returns a failure RunResult with Error == "Canceled.".
        var loop = new ConfigurableLoop(toThrow: new OperationCanceledException());
        var loopFactory = new StubLoopFactory(loop);
        var seeded = new List<ChatMessage> { ChatMessage.UserText("earlier") };
        using var session = this.NewSession(new FakeLlmClient(), loop, loopFactory, history: seeded);
        var before = session.History.Count;

        var result = await session.RunAsync("hi");

        Assert.False(result.Success);
        Assert.Equal("Canceled.", result.Error);
        Assert.Null(result.StopReason);
        Assert.Equal(1, loop.RunCalls);
        Assert.Equal(before, session.History.Count);
    }

    [Fact]
    public async Task RunAsync_compacts_before_the_turn_when_over_threshold()
    {
        // History over the AutoCompact threshold → compaction runs BEFORE the turn. Compaction is
        // the only thing that touches the fake client (the loop is faked), so a non-zero StreamCalls
        // proves it ran; and a successful compaction replaces history with summary + ack.
        var bigText = new string('x', 4_000); // ~1000 estimated tokens — well over the threshold below.
        var seeded = new List<ChatMessage> { ChatMessage.UserText(bigText) };
        var options = this.Options() with { AutoCompactTokenThreshold = 10 };
        var client = new FakeLlmClient();
        var loop = new ConfigurableLoop();
        using var session = this.NewSession(client, loop, new StubLoopFactory(loop), history: seeded, options: options);

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        // Compaction ran (forked summary call) before the turn.
        Assert.True(client.StreamCalls >= 1, "Auto-compaction should have invoked the client before the turn.");
        // Successful compaction replaced the big seeded history with summary + ack, then the turn
        // appended the user message → 3 messages total (2 compacted + 1 user turn).
        Assert.Equal(3, session.History.Count);
    }

    [Fact]
    public async Task RunAsync_does_not_compact_when_under_threshold()
    {
        // Small history under the threshold → compaction must NOT run (client untouched).
        var seeded = new List<ChatMessage> { ChatMessage.UserText("tiny") };
        var options = this.Options() with { AutoCompactTokenThreshold = 100_000 };
        var client = new FakeLlmClient();
        var loop = new ConfigurableLoop();
        using var session = this.NewSession(client, loop, new StubLoopFactory(loop), history: seeded, options: options);

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        Assert.Equal(0, client.StreamCalls);
        // History untouched by compaction: 1 seeded + 1 user turn.
        Assert.Equal(2, session.History.Count);
    }

    [Fact]
    public async Task RunAsync_accumulates_usage_and_persists_transcript_on_success()
    {
        var loop = new ConfigurableLoop();
        using var session = this.NewSession(new FakeLlmClient(), loop, new StubLoopFactory(loop));

        var first = await session.RunAsync("one");
        var second = await session.RunAsync("two");

        // Per-run Usage mirrors the loop's reported usage (7 in / 11 out).
        Assert.Equal(new TokenUsage(7, 11), first.Usage);
        // Session usage accumulates across runs.
        Assert.Equal(new TokenUsage(14, 22), session.SessionUsage);
        Assert.Equal("loop reply", second.FinalText);

        // The transcript was persisted for the session id (proves PersistTranscriptAsync ran).
        var transcriptPath = Path.Combine(this.root, ".coda", "sessions", session.SessionId + ".json");
        Assert.True(File.Exists(transcriptPath), $"Expected a persisted transcript at {transcriptPath}.");
    }

    [Fact]
    public async Task RunAsync_logs_transcript_persistence_failure_at_debug_and_completes()
    {
        // Force telemetry on into a temp dir so the swallowed transcript-write failure is observable.
        var logDir = Directory.CreateTempSubdirectory("coda_session_translog_").FullName;
        try
        {
            var options = this.Options() with
            {
                TelemetryOverride = Coda.Agent.Settings.TelemetrySettings.Disabled with
                {
                    Enabled = true,
                    MinLevel = LogLevel.Debug,
                    DirectoryOverride = logDir,
                    RetainedFileCount = 0,
                },
            };

            var loop = new ConfigurableLoop();
            var session = this.NewSession(new FakeLlmClient(), loop, new StubLoopFactory(loop), options: options);
            var logFilePath = session.LogFilePath;
            Assert.NotNull(logFilePath);

            // Block the transcript write deterministically: create a DIRECTORY exactly where the
            // transcript FILE must be written, so File.WriteAllTextAsync throws on save.
            var sessionsDir = Path.Combine(this.root, ".coda", "sessions");
            Directory.CreateDirectory(sessionsDir);
            var transcriptPath = Path.Combine(sessionsDir, session.SessionId + ".json");
            Directory.CreateDirectory(transcriptPath);

            // The run still SUCCEEDS — transcript persistence is best-effort and never breaks the turn.
            var result = await session.RunAsync("hi");
            Assert.True(result.Success);

            // Dispose flushes the logger factory so the JSON-lines file is fully written.
            await session.DisposeAsync();

            // Open with shared read/write: the writer may keep its handle briefly after dispose.
            using var fs = new FileStream(logFilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var logText = await reader.ReadToEndAsync();
            Assert.Contains("transcript persistence failed", logText);
            Assert.Contains(session.SessionId, logText);
            Assert.Contains("Debug", logText);
        }
        finally
        {
            try { Directory.Delete(logDir, recursive: true); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
