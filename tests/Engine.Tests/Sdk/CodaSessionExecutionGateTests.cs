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
/// Verifies <see cref="CodaSession"/> owns a stable <see cref="AgentExecutionGate"/>, passes it to
/// each turn's loop via the spec, and scopes the run inside
/// <see cref="AgentExecutionGate.BeginExecution"/> so the scope closes on success, error, AND
/// cancel — the gate is never left "executing" after a turn, restoring idle every time.
/// </summary>
public sealed class CodaSessionExecutionGateTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_session_gate_").FullName;

    private SessionOptions Options() => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = PermissionMode.BypassPermissions,
    };

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

    /// <summary>Records whether the gate reported "executing" while the run was in flight.</summary>
    private sealed class GateProbingLoop(AgentExecutionGate gate, Exception? toThrow) : IAgentLoop
    {
        public bool ObservedExecutingDuringRun { get; private set; }

        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default)
        {
            this.ObservedExecutingDuringRun = gate.IsExecuting;
            if (toThrow is not null)
            {
                throw toThrow;
            }

            sink.OnAssistantText("ok");
            sink.OnAssistantTextComplete();
            sink.OnStopReason("end_turn");
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLoopFactory(Exception? toThrow) : IAgentLoopFactory
    {
        public AgentExecutionGate? CapturedGate { get; private set; }

        public GateProbingLoop? Loop { get; private set; }

        public IAgentLoop Create(AgentLoopSpec spec)
        {
            this.CapturedGate = spec.Gate;
            this.Loop = new GateProbingLoop(spec.Gate!, toThrow);
            return this.Loop;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No real HTTP should be issued: all collaborators are faked.");
    }

    private CodaSession NewSession(CapturingLoopFactory factory) => new(
        SignedInClaude(),
        this.Options(),
        httpClient: new HttpClient(new ThrowingHandler()),
        llmClientFactory: new StubClientFactory(new StubClient()),
        agentLoopFactory: factory);

    [Fact]
    public async Task Successful_run_is_scoped_by_the_session_gate_and_ends_idle()
    {
        var factory = new CapturingLoopFactory(toThrow: null);
        using var session = this.NewSession(factory);

        Assert.False(session.ExecutionGate.IsExecuting);

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        // The loop was driven inside a BeginExecution scope: IsExecuting was true mid-run.
        Assert.True(factory.Loop!.ObservedExecutingDuringRun);
        // The spec carried the session's OWN stable gate instance.
        Assert.Same(session.ExecutionGate, factory.CapturedGate);
        // Scope closed on success: back to idle.
        Assert.False(session.ExecutionGate.IsExecuting);
    }

    [Fact]
    public async Task Faulted_run_still_closes_the_execution_scope()
    {
        var factory = new CapturingLoopFactory(toThrow: new InvalidOperationException("boom"));
        using var session = this.NewSession(factory);

        var result = await session.RunAsync("hi");

        Assert.False(result.Success);
        Assert.True(factory.Loop!.ObservedExecutingDuringRun);
        // Even though the loop threw, the using-scope closed: the gate is idle again.
        Assert.False(session.ExecutionGate.IsExecuting);
    }

    [Fact]
    public async Task Canceled_run_still_closes_the_execution_scope()
    {
        var factory = new CapturingLoopFactory(toThrow: new OperationCanceledException());
        using var session = this.NewSession(factory);

        var result = await session.RunAsync("hi");

        Assert.False(result.Success);
        Assert.Equal("Canceled.", result.Error);
        // Cancellation unwound through the using-scope: the gate is idle again.
        Assert.False(session.ExecutionGate.IsExecuting);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
