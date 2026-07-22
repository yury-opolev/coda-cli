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
    public void Steer_returns_the_accepted_entry_id_and_recall_is_ordered()
    {
        using var http = new HttpClient(new SseTestHandler(MessageStopOnly));
        using var session = new CodaSession(SignedInClaude(), this.Options(), httpClient: http);

        var firstId = session.Steer("first");
        var secondId = session.Steer("second");
        var recalled = session.RecallSteering();

        Assert.Equal([firstId, secondId], recalled.Select(entry => entry.Id));
        Assert.Equal(["first", "second"], recalled.Select(entry => entry.Text));
        Assert.Empty(session.RecallSteering());
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

    /// <summary>
    /// The <see cref="DefaultAgentLoopFactory"/> maps the spec's scheduled identity
    /// (<see cref="AgentLoopSpec.CurrentTaskId"/>/<see cref="AgentLoopSpec.CurrentDepth"/>) onto the
    /// real <see cref="AgentLoop"/> constructor, so a tool observes them via its
    /// <see cref="ToolContext"/>. Without the mapping the probe would see (null, 0).
    /// </summary>
    [Fact]
    public async Task DefaultAgentLoopFactory_maps_current_task_id_and_depth_to_the_tool_context()
    {
        var probe = new ContextProbeTool();
        var client = new ProbeScriptedClient();
        var spec = new AgentLoopSpec(
            client,
            new ToolRegistry([probe]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { Model = "m", WorkingDirectory = this.root, SystemPrompt = "sys" },
            Subagents: null,
            Hooks: null,
            Todos: null,
            Schedules: null,
            UserQuestion: null,
            UserHooks: null,
            PlanApprover: null,
            Lsp: null,
            LspDiagnostics: null,
            ToolSearch: null,
            Goal: null,
            CompactAsync: null,
            Logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            CurrentTaskId: "task-xyz",
            CurrentDepth: 2);

        var loop = new DefaultAgentLoopFactory().Create(spec);
        await loop.RunAsync([ChatMessage.UserText("go")], new CollectingNullSink(), CancellationToken.None);

        Assert.Equal("task-xyz", probe.SeenTaskId);
        Assert.Equal(2, probe.SeenDepth);
    }

    private sealed class ContextProbeTool : Coda.Agent.ITool
    {
        public string? SeenTaskId { get; private set; }

        public int SeenDepth { get; private set; } = -1;

        public string Name => "probe";

        public string Description => "records context";

        public string InputSchemaJson => "{}";

        public bool IsReadOnly => true;

        public Task<Coda.Agent.ToolResult> ExecuteAsync(System.Text.Json.JsonElement input, Coda.Agent.ToolContext context, CancellationToken cancellationToken = default)
        {
            this.SeenTaskId = context.CurrentTaskId;
            this.SeenDepth = context.CurrentDepth;
            return Task.FromResult(new Coda.Agent.ToolResult("ok"));
        }
    }

    private sealed class ProbeScriptedClient : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (this.turn++ == 0)
            {
                yield return AssistantStreamEvent.Tool(new ToolUseBlock("p1", "probe", "{}"));
                yield return AssistantStreamEvent.Finished("tool_use");
            }
            else
            {
                yield return AssistantStreamEvent.Delta("done");
                yield return AssistantStreamEvent.Finished("end_turn");
            }
        }
    }

    private sealed class CollectingNullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
