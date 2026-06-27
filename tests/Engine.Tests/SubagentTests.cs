using System.Runtime.CompilerServices;
using Coda.Agent;
using Coda.Agent.Tools;
using LlmClient;

namespace Engine.Tests;

public sealed class SubagentTests
{
    /// <summary>Yields pre-baked turns in order across successive StreamAsync calls.</summary>
    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

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

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    private static AgentOptions Options() =>
        new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" };

    [Fact]
    public async Task Task_tool_delegates_to_subagent_and_returns_its_report()
    {
        // Parent turn 1 -> call the task tool.
        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("t1", "task", """{"description":"do","prompt":"do the thing"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        // Subagent turn -> produce a report (no tools).
        var subagentTurn = new[]
        {
            AssistantStreamEvent.Delta("subagent report"),
            AssistantStreamEvent.Finished("end_turn"),
        };
        // Parent turn 2 -> wrap up.
        var parentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("all done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var client = new ScriptedClient(parentTurn1, subagentTurn, parentTurn2);
        var subagentTools = new ToolRegistry([]);
        var host = new SubagentHost(client, subagentTools, new AllowAllPermissionPrompt(), Options(), includeAnthropicSystemPrefix: false);
        var parentTools = new ToolRegistry([new TaskTool()]);
        var loop = new AgentLoop(client, parentTools, new AllowAllPermissionPrompt(), Options(), host);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // history: user, assistant(task), user(tool_result), assistant(text)
        var resultBlock = Assert.IsType<ToolResultBlock>(history[2].Content[0]);
        Assert.Equal("t1", resultBlock.ToolUseId);
        Assert.False(resultBlock.IsError);
        Assert.Equal("subagent report", resultBlock.Content);
    }

    [Fact]
    public async Task Task_tool_errors_when_no_subagent_host()
    {
        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("t1", "task", """{"description":"d","prompt":"x"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var parentTurn2 = new[] { AssistantStreamEvent.Finished("end_turn") };

        var client = new ScriptedClient(parentTurn1, parentTurn2);
        var loop = new AgentLoop(client, new ToolRegistry([new TaskTool()]), new AllowAllPermissionPrompt(), Options());

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        var resultBlock = Assert.IsType<ToolResultBlock>(history[2].Content[0]);
        Assert.True(resultBlock.IsError);
        Assert.Contains("not available", resultBlock.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Task_tool_is_not_in_the_default_built_in_set()
    {
        // BuiltInTools is the subagent set — it must NOT contain `task` (no infinite nesting).
        Assert.DoesNotContain(BuiltInTools.All(), t => t.Name == "task");
    }
}
