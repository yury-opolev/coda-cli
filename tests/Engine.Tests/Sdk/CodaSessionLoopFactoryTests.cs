using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Sdk;
using Engine.Tests.TestSupport;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using static Engine.Tests.TestSupport.CredentialFixtures;
using static Engine.Tests.TestSupport.SseTestHandler;

namespace Engine.Tests.Sdk;

/// <summary>
/// Verifies the <see cref="IAgentLoopFactory"/> seam: when a session is given a loop factory,
/// <see cref="CodaSession.RunAsync(string, IAgentSink?, System.Threading.CancellationToken)"/>
/// drives THAT factory's loop (built from an <see cref="AgentLoopSpec"/>), not a real
/// <see cref="AgentLoop"/>. The fake loop emits canned output the session must surface,
/// so the test fails if the seam is bypassed.
/// </summary>
public sealed class CodaSessionLoopFactoryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_session_loop_").FullName;

    private SessionOptions Options() => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = PermissionMode.BypassPermissions,
    };

    /// <summary>Fake loop: emits canned text to the sink instead of sampling a real model.</summary>
    private sealed class FakeAgentLoop : IAgentLoop
    {
        public const string CannedText = "from fake loop";

        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default)
        {
            sink.OnAssistantText(CannedText);
            sink.OnAssistantTextComplete();
            sink.OnStopReason("end_turn");
            return Task.CompletedTask;
        }
    }

    /// <summary>Records the spec it was handed and returns a fake loop, proving the seam is used.</summary>
    private sealed class RecordingLoopFactory : IAgentLoopFactory
    {
        public int CreateCalls { get; private set; }

        public AgentLoopSpec? LastSpec { get; private set; }

        public IAgentLoop Create(AgentLoopSpec spec)
        {
            this.CreateCalls++;
            this.LastSpec = spec;
            return new FakeAgentLoop();
        }
    }

    [Fact]
    public async Task RunAsync_drives_the_loop_from_the_injected_factory()
    {
        using var http = new HttpClient(new SseTestHandler(MessageStopOnly));
        var loopFactory = new RecordingLoopFactory();
        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            agentLoopFactory: loopFactory);

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        // The fake loop's canned text proves RunAsync drove THIS loop, not a real AgentLoop.
        Assert.Equal(FakeAgentLoop.CannedText, result.FinalText);
        Assert.Equal(1, loopFactory.CreateCalls);
        Assert.NotNull(loopFactory.LastSpec);
        // The spec carries the expected per-turn construction values.
        Assert.Equal(ClaudeAiProvider.Id, loopFactory.LastSpec!.Client.ProviderId);
        Assert.NotNull(loopFactory.LastSpec.Tools);
        Assert.NotNull(loopFactory.LastSpec.Permissions);
        Assert.Equal("claude-sonnet-4-6", loopFactory.LastSpec.Options.Model);
    }

    [Fact]
    public void DefaultAgentLoopFactory_builds_a_real_AgentLoop_from_the_spec()
    {
        using var http = new HttpClient(new SseTestHandler(MessageStopOnly));
        var client = LlmClientFactory.Create(ClaudeAiProvider.Id, SignedInClaude(), new ClientFingerprint(), http)!;
        var spec = new AgentLoopSpec(
            client,
            new ToolRegistry([]),
            new ModePermissionPrompt(PermissionMode.BypassPermissions, null),
            new AgentOptions { Model = "claude-sonnet-4-6", WorkingDirectory = this.root, SystemPrompt = "test" },
            Subagents: null,
            Hooks: null,
            Todos: null,
            Schedules: null,
            UserQuestion: null,
            UserHooks: null,
            PlanApprover: null,
            BackgroundTasks: null,
            Lsp: null,
            LspDiagnostics: null,
            ToolSearch: null,
            Goal: null,
            CompactAsync: null,
            Logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        IAgentLoopFactory factory = new DefaultAgentLoopFactory();
        var loop = factory.Create(spec);

        Assert.IsType<AgentLoop>(loop);
        Assert.Null(loop.LastGoalStatus);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
