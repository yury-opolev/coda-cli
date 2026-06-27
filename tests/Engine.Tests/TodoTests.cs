using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;

namespace Engine.Tests;

public sealed class TodoTests
{
    [Fact]
    public void Store_set_replaces_items()
    {
        var store = new TodoStore();
        store.Set([new TodoItem("a", "doing a", TodoStatus.Pending)]);
        store.Set([new TodoItem("b", "doing b", TodoStatus.Completed)]);
        Assert.Single(store.Items);
        Assert.Equal("b", store.Items[0].Content);
        Assert.Equal(TodoStatus.Completed, store.Items[0].Status);
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task TodoWrite_stores_items_and_renders_checklist()
    {
        var store = new TodoStore();
        var ctx = new ToolContext(".") { Todos = store };
        var tool = new TodoWriteTool();
        var input = Json("""
            {"todos":[
              {"content":"Write tests","activeForm":"Writing tests","status":"completed"},
              {"content":"Implement","activeForm":"Implementing","status":"in_progress"},
              {"content":"Review","activeForm":"Reviewing","status":"pending"}
            ]}
            """);

        var result = await tool.ExecuteAsync(input, ctx);

        Assert.False(result.IsError);
        Assert.Equal(3, store.Items.Count);
        Assert.Equal(TodoStatus.Completed, store.Items[0].Status);
        // Rendered checklist markers.
        Assert.Contains("[x] Write tests", result.Content);
        Assert.Contains("[~] Implementing", result.Content);  // in_progress shows active form
        Assert.Contains("[ ] Review", result.Content);
    }

    [Fact]
    public async Task TodoWrite_without_store_is_graceful()
    {
        var tool = new TodoWriteTool();
        var input = Json("""{"todos":[{"content":"x","activeForm":"x-ing","status":"pending"}]}""");
        var result = await tool.ExecuteAsync(input, new ToolContext("."));
        Assert.False(result.IsError); // no store (e.g. subagent) → no-op, not an error
        Assert.Contains("[ ] x", result.Content);
    }

    [Fact]
    public async Task TodoWrite_rejects_empty()
    {
        var tool = new TodoWriteTool();
        var result = await tool.ExecuteAsync(Json("""{"todos":[]}"""), new ToolContext(".") { Todos = new TodoStore() });
        Assert.True(result.IsError);
    }

    [Fact]
    public void TodoWrite_is_read_only_and_named()
    {
        var tool = new TodoWriteTool();
        Assert.Equal("todo_write", tool.Name);
        Assert.True(tool.IsReadOnly);
    }

    private sealed class ScriptedClient(params IReadOnlyList<LlmClient.AssistantStreamEvent>[] turns) : LlmClient.ILlmClient
    {
        private int turn;
        public string ProviderId => "fake";
        public async IAsyncEnumerable<LlmClient.AssistantStreamEvent> StreamAsync(
            LlmClient.ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = turns[Math.Min(this.turn, turns.Length - 1)];
            this.turn++;
            foreach (var e in events) { await Task.Yield(); yield return e; }
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

    [Fact]
    public async Task AgentLoop_threads_the_todo_store_to_the_tool()
    {
        var toolTurn = new[]
        {
            LlmClient.AssistantStreamEvent.Tool(new LlmClient.ToolUseBlock("t1", "todo_write",
                """{"todos":[{"content":"Step one","activeForm":"Doing step one","status":"in_progress"}]}""")),
            LlmClient.AssistantStreamEvent.Finished("tool_use"),
        };
        var endTurn = new[] { LlmClient.AssistantStreamEvent.Delta("done"), LlmClient.AssistantStreamEvent.Finished("end_turn") };

        var store = new TodoStore();
        var loop = new AgentLoop(
            new ScriptedClient(toolTurn, endTurn),
            new ToolRegistry([new TodoWriteTool()]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" },
            todos: store);

        var history = new List<LlmClient.ChatMessage> { LlmClient.ChatMessage.UserText("plan it") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.Single(store.Items);
        Assert.Equal("Step one", store.Items[0].Content);
        Assert.Equal(TodoStatus.InProgress, store.Items[0].Status);
    }
}
