using System.Runtime.CompilerServices;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Engine.Tests.TestSupport;

/// <summary>
/// Fake provider client. Records how many times <see cref="StreamAsync"/> is called (only
/// compaction calls it here, since the loop is faked) and emits canned text so a forked
/// compaction summary is non-empty.
/// </summary>
internal sealed class FakeLlmClient : ILlmClient
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
internal sealed class StubClientFactory : ILlmClientFactory
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
internal sealed class ConfigurableLoop : IAgentLoop
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
internal sealed class StubLoopFactory : IAgentLoopFactory
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

/// <summary>Asserts no real HTTP ever leaks out: every collaborator is faked.</summary>
internal sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No real HTTP should be issued: all collaborators are faked.");
}

/// <summary>
/// Shared builder for a <see cref="CodaSession"/> wired to fake collaborators (no real
/// HTTP/LLM/loop). Extracted from <c>CodaSessionRunAsyncTests</c> so other test classes can build
/// a working, fully-faked session without duplicating the fake collaborators.
/// </summary>
internal static class FakeSession
{
    /// <summary>
    /// Builds a CodaSession over <paramref name="workingDirectory"/> with all collaborators faked
    /// (no real HTTP/LLM/loop). The fake loop emits canned text + TokenUsage(7,11) + stopReason
    /// "end_turn" so a turn "succeeds".
    /// </summary>
    public static CodaSession New(
        string workingDirectory,
        string? sessionId = null,
        List<ChatMessage>? history = null,
        string? systemPromptOverride = null)
    {
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = workingDirectory,
            PermissionMode = PermissionMode.BypassPermissions,
            SystemPromptOverride = systemPromptOverride,
        };
        var loop = new ConfigurableLoop();
        return new CodaSession(
            CredentialFixtures.SignedInClaude(),
            options,
            httpClient: new HttpClient(new ThrowingHandler()),
            history: history,
            sessionId: sessionId,
            llmClientFactory: new StubClientFactory(new FakeLlmClient()),
            agentLoopFactory: new StubLoopFactory(loop));
    }
}
