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
/// Integration coverage proving the session execution scope spans ALL agentic work in a turn,
/// including pre-turn auto-compaction (a forked model call) — not just the agent loop. A gated
/// fake client parks inside the compaction stream so the test can observe
/// <see cref="AgentExecutionGate.IsExecuting"/> while compaction is in flight and confirm a pause
/// requested during compaction is NOT reported reached until compaction finishes and the turn ends.
/// </summary>
public sealed class CodaSessionCompactionGateTests : IDisposable
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NonCompletionWindow = TimeSpan.FromMilliseconds(150);

    private readonly string root = Directory.CreateTempSubdirectory("coda_session_compact_gate_").FullName;

    private SessionOptions Options() => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = PermissionMode.BypassPermissions,
    };

    private static Task ShouldComplete(Task task) => task.WaitAsync(CompletionTimeout);

    private static async Task ShouldStayParked(Task task)
    {
        var delay = Task.Delay(NonCompletionWindow);
        var first = await Task.WhenAny(task, delay);
        Assert.Same(delay, first);
    }

    /// <summary>
    /// Fake client whose stream (the only client call in this test is the forked compaction call)
    /// signals when it has started and then parks until released — so the test controls exactly how
    /// long pre-turn compaction is in flight.
    /// </summary>
    private sealed class GatedCompactionClient : ILlmClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => ClaudeAiProvider.Id;

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.Started.TrySetResult();
            await this.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return AssistantStreamEvent.Delta("compacted summary");
            yield return AssistantStreamEvent.Finished("end_turn");
        }
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

    /// <summary>Fake loop that records the gate's executing state and offers no pause boundary.</summary>
    private sealed class ProbeLoop(AgentExecutionGate gate) : IAgentLoop
    {
        public bool ObservedExecutingDuringRun { get; private set; }

        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default)
        {
            this.ObservedExecutingDuringRun = gate.IsExecuting;
            sink.OnAssistantText("ok");
            sink.OnAssistantTextComplete();
            sink.OnStopReason("end_turn");
            return Task.CompletedTask;
        }
    }

    private sealed class ProbeLoopFactory : IAgentLoopFactory
    {
        public ProbeLoop? Loop { get; private set; }

        public IAgentLoop Create(AgentLoopSpec spec)
        {
            this.Loop = new ProbeLoop(spec.Gate!);
            return this.Loop;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No real HTTP should be issued: all collaborators are faked.");
    }

    [Fact]
    public async Task Pre_turn_compaction_runs_inside_the_execution_scope_and_defers_pause_reached()
    {
        // A large seeded history over the (tiny) auto-compact threshold forces pre-turn compaction,
        // which is the only thing that touches the gated client (the loop is faked).
        var bigText = new string('x', 4_000);
        var seeded = new List<ChatMessage> { ChatMessage.UserText(bigText) };
        var options = this.Options() with { AutoCompactTokenThreshold = 10 };
        var client = new GatedCompactionClient();
        var factory = new ProbeLoopFactory();
        using var session = new CodaSession(
            SignedInClaude(),
            options,
            httpClient: new HttpClient(new ThrowingHandler()),
            history: seeded,
            llmClientFactory: new StubClientFactory(client),
            agentLoopFactory: factory);

        // Start the turn; it blocks inside pre-turn compaction (the gated client stream).
        var run = session.RunAsync("hi");
        await ShouldComplete(client.Started.Task);

        // Compaction is agentic work: the execution scope must already be open around it.
        Assert.True(session.ExecutionGate.IsExecuting);

        // A pause requested DURING compaction must not be reported reached — compaction is running
        // and no safe boundary or turn-end has occurred yet.
        using var lease = session.ExecutionGate.RequestPause();
        var reached = session.ExecutionGate.WaitUntilPaused(CancellationToken.None);
        await ShouldStayParked(reached);

        // Release compaction. The faked loop offers no boundary, so the turn ends → reached fires.
        client.Release.TrySetResult();
        await ShouldComplete(reached);

        var result = await run.WaitAsync(CompletionTimeout);
        Assert.True(result.Success);
        Assert.True(factory.Loop!.ObservedExecutingDuringRun);
        Assert.False(session.ExecutionGate.IsExecuting);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
