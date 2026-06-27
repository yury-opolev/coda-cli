using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Teams;
using Coda.Agent.Tools;
using LlmClient;

namespace Engine.Tests.Teams;

/// <summary>
/// Integration tests for the leader inbox drain seam in AgentLoop (Task 10A) and
/// for the team tool registry presence requirement.
/// All waits are bounded. No real LLM; no real ~/.coda.
/// </summary>
public sealed class TeamIntegrationTests : IDisposable
{
    private readonly string tempDir;

    public TeamIntegrationTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-teamint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    // ── Stubs / fakes ─────────────────────────────────────────────────────────

    /// <summary>
    /// Scripted LLM client: turn 0 → emits a tool use; turn 1 → emits text + end_turn.
    /// Allows the AgentLoop to complete after exactly one tool cycle.
    /// </summary>
    private sealed class TwoTurnClient : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var thisTurn = this.turn++;

            if (thisTurn == 0)
            {
                await Task.Yield();
                yield return AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", "echo", "{}"));
                yield return AssistantStreamEvent.Finished("tool_use");
            }
            else
            {
                await Task.Yield();
                yield return AssistantStreamEvent.Delta("leader done");
                yield return AssistantStreamEvent.Finished("end_turn");
            }
        }
    }

    /// <summary>
    /// LLM client that emits a single text turn (no tool use). Used to test
    /// the inbox-drain seam without a tool cycle — we need iteration > 0 which
    /// requires at least one tool use first, so we use TwoTurnClient for that.
    /// </summary>
    private sealed class SingleTurnClient : ILlmClient
    {
        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return AssistantStreamEvent.Delta("done");
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }

    private sealed class EchoTool : ITool
    {
        public string Name => "echo";
        public string Description => "echo";
        public string InputSchemaJson => """{"type":"object"}""";
        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult("echoed"));
    }

    private sealed class CaptureSink : IAgentSink
    {
        private readonly List<string> texts = [];
        public IReadOnlyList<string> Texts => this.texts;

        public void OnAssistantText(string delta) => this.texts.Add(delta);
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
        public void OnStopReason(string? stopReason) { }
    }

    private static AgentOptions Options(string workDir) => new()
    {
        SystemPrompt = "sys",
        WorkingDirectory = workDir,
        Model = "m",
    };

    private TeamManager MakeManager(Func<TeammateIdentity, string, ITeammateAgent>? factory = null)
    {
        factory ??= (_, _) => new NeverRunAgent();
        return new TeamManager(this.tempDir, factory);
    }

    private sealed class NeverRunAgent : ITeammateAgent
    {
        public async Task<string> RunTurnAsync(string prompt, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return "cancelled";
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// When there is a pending message in the leader inbox AND the loop has
    /// completed at least one iteration (iteration > 0), the AgentLoop must
    /// inject it as a User ChatMessage before the next model call.
    /// Strategy: use TwoTurnClient so iteration advances past 0, and write a
    /// message into the leader mailbox before the loop runs; capture history
    /// after RunAsync and assert a <teammate_message> block appears.
    /// </summary>
    [Fact]
    public async Task Leader_inbox_drain_injects_teammate_message_into_history()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var manager = this.MakeManager();
        manager.CreateTeam("t", null);

        // Pre-populate the leader inbox with a plain message.
        var mailbox = new Mailbox(this.tempDir);
        var msg = new TeammateMessage(
            From: "alice",
            Text: "hello from alice",
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Read: false,
            Color: "blue",
            Summary: null);
        await mailbox.WriteAsync(TeamConstants.TeamLeadName, "t", msg, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        var loop = new AgentLoop(
            new TwoTurnClient(),
            new ToolRegistry([new EchoTool()]),
            new AllowAllPermissionPrompt(),
            Options(this.tempDir),
            teams: manager);

        var history = new List<ChatMessage> { ChatMessage.UserText("start") };
        var sink = new CaptureSink();

        await loop.RunAsync(history, sink, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(15), cts.Token);

        // At least one injected user message after iteration 0 must contain <teammate_message>.
        var inboxMessages = history
            .Where(m => m.Role == ChatRole.User)
            .SelectMany(m => m.Content.OfType<TextBlock>().Select(b => b.Text))
            .ToList();

        Assert.Contains(inboxMessages, text =>
            text.Contains("<teammate_message", StringComparison.Ordinal) &&
            text.Contains("hello from alice", StringComparison.Ordinal));
    }

    /// <summary>
    /// When teams is null (no TeamManager), the loop runs normally without errors.
    /// Regression guard: no inbox seam is triggered.
    /// </summary>
    [Fact]
    public async Task AgentLoop_without_teams_completes_normally()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var loop = new AgentLoop(
            new SingleTurnClient(),
            new ToolRegistry([new EchoTool()]),
            new AllowAllPermissionPrompt(),
            Options(this.tempDir));

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, new CaptureSink(), cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(8), cts.Token);

        Assert.Equal(2, history.Count); // user + assistant
    }

    /// <summary>
    /// Verifies that the standard team tool names are resolvable in a ToolRegistry
    /// that includes the team tools. This is the "tools present in registry" requirement.
    /// </summary>
    [Fact]
    public void Team_tools_present_in_registry()
    {
        var teamTools = new ITool[]
        {
            new TeamCreateTool(),
            new SpawnTeammateTool(),
            new TeamDeleteTool(),
            new SendMessageTool(),
            new TaskCreateTool(),
            new TaskListTool(),
            new TaskGetTool(),
            new TaskUpdateTool(),
            new TaskStopTool(),
        };

        var registry = new ToolRegistry([.. BuiltInTools.All(), .. teamTools]);

        var expectedNames = new[]
        {
            "team_create",
            "spawn_teammate",
            "team_delete",
            "send_message",
            "task_create",
            "task_list",
            "task_get",
            "task_update",
            "task_stop",
        };

        foreach (var name in expectedNames)
        {
            var tool = registry.Resolve(name);
            Assert.NotNull(tool);
            Assert.Equal(name, tool!.Name);
        }
    }
}
