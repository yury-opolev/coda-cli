using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Verifies that a parent turn's tool restriction propagates monotonically to child subagents
/// (Finding 1), and that per-turn overrides for system prompt, model, and effort are
/// intentionally NOT propagated — only the tool restriction crosses the boundary.
/// </summary>
public sealed class SubagentRestrictionTests
{
    /// <summary>
    /// Records every <see cref="ChatRequest"/> it receives and yields scripted turns in order.
    /// </summary>
    private sealed class RecordingClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

        public List<ChatRequest> Requests { get; } = [];

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.Requests.Add(request);
            var events = turns[this.turn++];
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class FakeTool(string name) : ITool
    {
        public string Name => name;

        public string Description => name;

        public string InputSchemaJson => "{}";

        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult("ok"));
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }

        public void OnAssistantTextComplete() { }

        public void OnToolCall(string toolName, string inputPreview) { }

        public void OnToolResult(string toolName, ToolResult result) { }

        public void OnError(string message) { }
    }

    private static AgentOptions Options(string model = "test-model") =>
        new() { SystemPrompt = "session-system", WorkingDirectory = ".", Model = model };

    // =========================================================================
    // Test 1 — parent-denied tool is not advertised in the child subagent turn
    // =========================================================================

    [Fact]
    public async Task Parent_denied_tool_is_not_advertised_in_child_subagent()
    {
        // Parent turn 1: calls the `task` tool.
        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("t1", "task", """{"description":"d","prompt":"p"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        // Subagent turn: produces text (no tool calls).
        var subagentTurn = new[] { AssistantStreamEvent.Delta("done"), AssistantStreamEvent.Finished("end_turn") };
        // Parent turn 2: ends.
        var parentTurn2 = new[] { AssistantStreamEvent.Finished("end_turn") };

        var client = new RecordingClient(parentTurn1, subagentTurn, parentTurn2);

        // The subagent's registry includes both the denied tool and read_file.
        const string DeniedTool = "run_command";
        var subagentTools = new ToolRegistry([new FakeTool(DeniedTool), new FakeTool("read_file")]);
        var mgr = new TaskManager(sessionId: "s", logRoot: null);
        var host = new SubagentHost(client, subagentTools, new AllowAllPermissionPrompt(), Options(), mgr, includeAnthropicSystemPrefix: false);

        // Parent registry: task tool + the denied tool (so the parent resolution makes DeniedTools non-empty).
        var parentTools = new ToolRegistry([new TaskTool(), new FakeTool(DeniedTool)]);
        var loop = new AgentLoop(client, parentTools, new AllowAllPermissionPrompt(), Options(), host, tasks: mgr);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        // Run the parent with a shape that explicitly denies DeniedTool.
        await loop.RunAsync(history, new NullSink(), CancellationToken.None,
            new TurnShape { DeniedTools = [DeniedTool] });

        // Requests: [0] parent turn 1, [1] subagent turn, [2] parent turn 2.
        Assert.Equal(3, client.Requests.Count);
        var childRequest = client.Requests[1];

        // The denied tool must NOT appear in what the child model sees.
        Assert.DoesNotContain(childRequest.Tools ?? [], t => t.Name == DeniedTool);
        // But read_file (not denied) should still appear.
        Assert.Contains(childRequest.Tools ?? [], t => t.Name == "read_file");
    }

    // =========================================================================
    // Test 2 — parent SystemPrompt / Model / Effort do NOT reach the child
    // =========================================================================

    [Fact]
    public async Task Parent_shape_system_prompt_model_effort_do_not_reach_child_subagent()
    {
        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("t1", "task", """{"description":"d","prompt":"p"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var subagentTurn = new[] { AssistantStreamEvent.Delta("done"), AssistantStreamEvent.Finished("end_turn") };
        var parentTurn2 = new[] { AssistantStreamEvent.Finished("end_turn") };

        const string ParentSystemOverride = "EVIL-PARENT-SYSTEM";
        const string ParentModelOverride = "evil-parent-model";
        const string SessionModel = "session-model";

        var client = new RecordingClient(parentTurn1, subagentTurn, parentTurn2);

        var subagentTools = new ToolRegistry([new FakeTool("read_file")]);
        var mgr = new TaskManager(sessionId: "s", logRoot: null);
        var options = Options(SessionModel);
        var host = new SubagentHost(client, subagentTools, new AllowAllPermissionPrompt(), options, mgr, includeAnthropicSystemPrefix: false);

        var parentTools = new ToolRegistry([new TaskTool()]);
        var loop = new AgentLoop(client, parentTools, new AllowAllPermissionPrompt(), options, host, tasks: mgr);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        // Parent turn with full shape overrides — SystemPrompt + Model + Effort + a tool denial.
        await loop.RunAsync(history, new NullSink(), CancellationToken.None,
            new TurnShape
            {
                SystemPrompt = ParentSystemOverride,
                Model = ParentModelOverride,
                Effort = "max",
                DeniedTools = ["fake_denied"],
            });

        Assert.Equal(3, client.Requests.Count);
        var childRequest = client.Requests[1];

        // Child must use the subagent's own system prompt — NOT the parent's override.
        Assert.DoesNotContain(ParentSystemOverride, childRequest.System ?? string.Empty, StringComparison.Ordinal);
        // Child must use the session model — NOT the parent's model override.
        Assert.NotEqual(ParentModelOverride, childRequest.Model);
        Assert.Equal(SessionModel, childRequest.Model);
    }
}
