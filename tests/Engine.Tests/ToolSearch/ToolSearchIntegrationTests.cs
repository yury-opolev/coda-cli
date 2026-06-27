using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.ToolSearch;
using Coda.Agent.Tools;
using LlmClient;

namespace Engine.Tests.ToolSearch;

/// <summary>
/// Integration tests for AgentLoop + ToolSearchCoordinator wiring:
/// - active mode excludes deferred tools from wire and includes tool_search
/// - first request carries a &lt;deferred-tools&gt; reminder
/// - after tool_search discovers a tool, next request includes it
/// - standard mode is byte-identical to today (no reminder, all tools inline)
/// </summary>
public sealed class ToolSearchIntegrationTests
{
    // ── Shared helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// A capturing scripted client: records the full ChatRequest per call (so tests
    /// can assert on the tool definitions and messages sent), and returns pre-scripted turns.
    /// </summary>
    private sealed class CapturingScriptedClient : ILlmClient
    {
        private readonly IReadOnlyList<AssistantStreamEvent>[] turns;
        private int turn;
        private readonly List<ChatRequest> requests = new();

        public CapturingScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns)
        {
            this.turns = turns;
        }

        public string ProviderId => "fake";

        /// <summary>All requests that were made, in order.</summary>
        public IReadOnlyList<ChatRequest> Requests => this.requests;

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.requests.Add(request);
            var events = this.turns[this.turn++];
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

    /// <summary>A non-deferred read-only stub ("read_file").</summary>
    private sealed class ReadFileTool : ITool
    {
        public string Name => "read_file";
        public string Description => "Read a file from disk.";
        public string InputSchemaJson => """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""";
        public bool IsReadOnly => true;
        public bool ShouldDefer => false;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult("file contents"));
    }

    /// <summary>A deferred stub ("mcp__demo__do", ShouldDefer = true).</summary>
    private sealed class DeferredDemoTool : ITool
    {
        public string Name => "mcp__demo__do";
        public string Description => "Demo MCP deferred tool.";
        public string InputSchemaJson => """{"type":"object","properties":{"input":{"type":"string"}},"required":["input"]}""";
        public bool IsReadOnly => true;
        public bool ShouldDefer => true;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult("demo result"));
    }

    private static AgentOptions Options() => new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" };

    private static ToolRegistry MakeRegistry()
        => new([new ReadFileTool(), new DeferredDemoTool(), new ToolSearchTool()]);

    private static AssistantStreamEvent[] StopTurn()
        => [AssistantStreamEvent.Finished("end_turn")];

    private static AssistantStreamEvent[] ToolSearchTurn(string query)
    {
        var inputJson = $$$"""{"query": "{{{query}}}"}""";
        return
        [
            AssistantStreamEvent.Tool(new ToolUseBlock("ts_1", ToolSearchToolNames.ToolSearch, inputJson)),
            AssistantStreamEvent.Finished("tool_use"),
        ];
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// When mode is active (Tst), the first model request's tool definitions must
    /// include "read_file" and "tool_search" but NOT the deferred "mcp__demo__do".
    /// </summary>
    [Fact]
    public async Task Active_mode_wire_excludes_deferred_includes_tool_search()
    {
        var client = new CapturingScriptedClient(StopTurn());
        var registry = MakeRegistry();
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);

        var loop = new AgentLoop(
            client,
            registry,
            new AllowAllPermissionPrompt(),
            Options(),
            toolSearch: coordinator);

        var history = new List<ChatMessage> { ChatMessage.UserText("hello") };
        await loop.RunAsync(history, new NullSink());

        Assert.Single(client.Requests);
        var wireNames = client.Requests[0].Tools!.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("read_file", wireNames);
        Assert.Contains(ToolSearchToolNames.ToolSearch, wireNames);
        Assert.DoesNotContain("mcp__demo__do", wireNames);
    }

    /// <summary>
    /// The history sent on the first request must contain a &lt;deferred-tools&gt; block
    /// naming "mcp__demo__do" (injected as a ChatRole.User TextBlock before the model call).
    /// </summary>
    [Fact]
    public async Task First_request_carries_deferred_tools_reminder()
    {
        var client = new CapturingScriptedClient(StopTurn());
        var registry = MakeRegistry();
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);

        var loop = new AgentLoop(
            client,
            registry,
            new AllowAllPermissionPrompt(),
            Options(),
            toolSearch: coordinator);

        var history = new List<ChatMessage> { ChatMessage.UserText("hello") };
        await loop.RunAsync(history, new NullSink());

        Assert.Single(client.Requests);
        var messages = client.Requests[0].Messages;

        // Collect all text blocks from user messages
        var userTextBlocks = messages
            .Where(m => m.Role == ChatRole.User)
            .SelectMany(m => m.Content.OfType<TextBlock>())
            .Select(tb => tb.Text)
            .ToList();

        var reminderBlock = userTextBlocks.FirstOrDefault(t => t.Contains("<deferred-tools>"));
        Assert.NotNull(reminderBlock);
        Assert.Contains("mcp__demo__do", reminderBlock);
    }

    /// <summary>
    /// After the model calls tool_search with "select:mcp__demo__do" on request 1,
    /// the next request (request 2) must include "mcp__demo__do" in wire tool definitions.
    /// </summary>
    [Fact]
    public async Task After_tool_search_discovers_tool_next_request_includes_it()
    {
        var client = new CapturingScriptedClient(
            ToolSearchTurn("select:mcp__demo__do"),   // turn 1: call tool_search
            StopTurn());                              // turn 2: stop

        var registry = MakeRegistry();
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);

        var loop = new AgentLoop(
            client,
            registry,
            new AllowAllPermissionPrompt(),
            Options(),
            toolSearch: coordinator);

        var history = new List<ChatMessage> { ChatMessage.UserText("hello") };
        await loop.RunAsync(history, new NullSink());

        Assert.Equal(2, client.Requests.Count);

        // Request 1 must NOT have mcp__demo__do
        var wire1 = client.Requests[0].Tools!.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("mcp__demo__do", wire1);

        // Request 2 must have mcp__demo__do (discovered via tool_search)
        var wire2 = client.Requests[1].Tools!.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("mcp__demo__do", wire2);
    }

    /// <summary>
    /// Standard mode (no coordinator) must pass all tools inline and must NOT inject
    /// any &lt;deferred-tools&gt; reminder into history.
    /// </summary>
    [Fact]
    public async Task Standard_mode_all_tools_inline_no_reminder()
    {
        var client = new CapturingScriptedClient(StopTurn());
        var registry = MakeRegistry();

        // No toolSearch coordinator passed — standard behavior
        var loop = new AgentLoop(
            client,
            registry,
            new AllowAllPermissionPrompt(),
            Options());

        var history = new List<ChatMessage> { ChatMessage.UserText("hello") };
        await loop.RunAsync(history, new NullSink());

        Assert.Single(client.Requests);
        var wireNames = client.Requests[0].Tools!.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        // All three tools inline (including mcp__demo__do)
        Assert.Contains("read_file", wireNames);
        Assert.Contains("mcp__demo__do", wireNames);
        Assert.Contains(ToolSearchToolNames.ToolSearch, wireNames);

        // No <deferred-tools> reminder in history
        var messages = client.Requests[0].Messages;
        var userTextBlocks = messages
            .Where(m => m.Role == ChatRole.User)
            .SelectMany(m => m.Content.OfType<TextBlock>())
            .Select(tb => tb.Text)
            .ToList();

        Assert.DoesNotContain(userTextBlocks, t => t.Contains("<deferred-tools>"));
    }
}
