using System.Text.Json;
using Coda.Agent;
using Coda.Agent.ToolSearch;
using Coda.Agent.Tools;

namespace Engine.Tests.ToolSearch;

public sealed class ToolSearchToolTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class StubTool : ITool
    {
        public StubTool(
            string name,
            string description = "stub description",
            string inputSchemaJson = """{"type":"object","properties":{}}""",
            bool shouldDefer = false)
        {
            this.Name = name;
            this.Description = description;
            this.InputSchemaJson = inputSchemaJson;
            this.shouldDefer = shouldDefer;
        }

        private readonly bool shouldDefer;

        public string Name { get; }
        public string Description { get; }
        public string InputSchemaJson { get; }
        public bool IsReadOnly => true;
        public bool ShouldDefer => this.shouldDefer;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private static ToolContext MakeContext(IReadOnlyList<ITool> tools, List<IReadOnlyList<string>> captured)
    {
        return new ToolContext("/tmp")
        {
            AllTools = tools,
            OnToolsDiscovered = names => captured.Add(names),
        };
    }

    private static JsonElement MakeInput(string query, int? maxResults = null)
    {
        var json = maxResults.HasValue
            ? $$"""{"query": "{{query}}", "max_results": {{maxResults.Value}}}"""
            : $$"""{"query": "{{query}}"}""";
        return JsonDocument.Parse(json).RootElement;
    }

    private readonly ToolSearchTool tool = new();

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Select_returns_functions_block()
    {
        var slackTool = new StubTool(
            "mcp__slack__send",
            "Send a slack message",
            """{"type":"object","properties":{"text":{"type":"string"}}}""",
            shouldDefer: true);

        var nonDeferred = new StubTool("some_other_tool", shouldDefer: false);
        IReadOnlyList<ITool> allTools = [slackTool, nonDeferred, this.tool];

        var captured = new List<IReadOnlyList<string>>();
        var context = MakeContext(allTools, captured);
        var input = MakeInput("select:mcp__slack__send");

        var result = await this.tool.ExecuteAsync(input, context);

        Assert.False(result.IsError);
        Assert.Contains("<functions>", result.Content);
        Assert.Contains("<function>", result.Content);
        Assert.Contains("</function>", result.Content);
        Assert.Contains("</functions>", result.Content);
        Assert.Contains("\"name\": \"mcp__slack__send\"", result.Content);
        Assert.Contains("Send a slack message", result.Content);
        Assert.Contains("\"text\"", result.Content);

        // OnToolsDiscovered should have been called with the matched name
        Assert.Single(captured);
        Assert.Equal(["mcp__slack__send"], captured[0]);
    }

    [Fact]
    public async Task Keyword_match_returns_block()
    {
        var notebookTool = new StubTool(
            "mcp__jupyter__execute",
            "Execute a Jupyter notebook cell",
            """{"type":"object","properties":{"code":{"type":"string"}}}""",
            shouldDefer: true);

        IReadOnlyList<ITool> allTools = [notebookTool, this.tool];
        var captured = new List<IReadOnlyList<string>>();
        var context = MakeContext(allTools, captured);
        var input = MakeInput("notebook jupyter");

        var result = await this.tool.ExecuteAsync(input, context);

        Assert.False(result.IsError);
        Assert.Contains("<functions>", result.Content);
        Assert.Contains("mcp__jupyter__execute", result.Content);

        Assert.Single(captured);
        Assert.Contains("mcp__jupyter__execute", captured[0]);
    }

    [Fact]
    public async Task No_match_returns_note()
    {
        var irrelevantTool = new StubTool("mcp__github__list_prs", "List GitHub PRs", shouldDefer: true);
        IReadOnlyList<ITool> allTools = [irrelevantTool, this.tool];

        var captured = new List<IReadOnlyList<string>>();
        var context = MakeContext(allTools, captured);
        var input = MakeInput("select:nonexistent");

        var result = await this.tool.ExecuteAsync(input, context);

        Assert.False(result.IsError);
        Assert.Equal("No matching deferred tools found.", result.Content);

        // OnToolsDiscovered should have been called with empty list
        Assert.Single(captured);
        Assert.Empty(captured[0]);
    }

    [Fact]
    public async Task Missing_query_is_error()
    {
        IReadOnlyList<ITool> allTools = [this.tool];
        var captured = new List<IReadOnlyList<string>>();
        var context = MakeContext(allTools, captured);
        var input = JsonDocument.Parse("""{"max_results": 5}""").RootElement;

        var result = await this.tool.ExecuteAsync(input, context);

        Assert.True(result.IsError);
        Assert.Contains("query is required", result.Content);
    }

    [Fact]
    public async Task Functions_line_is_valid_json()
    {
        var slackTool = new StubTool(
            "mcp__slack__send",
            "Send a slack message",
            """{"type":"object","properties":{"text":{"type":"string"}}}""",
            shouldDefer: true);

        IReadOnlyList<ITool> allTools = [slackTool, this.tool];
        var captured = new List<IReadOnlyList<string>>();
        var context = MakeContext(allTools, captured);
        var input = MakeInput("select:mcp__slack__send");

        var result = await this.tool.ExecuteAsync(input, context);

        // Find the <function>...</function> line
        var content = result.Content;
        var funcStart = content.IndexOf("<function>", StringComparison.Ordinal);
        var funcEnd = content.IndexOf("</function>", StringComparison.Ordinal);
        Assert.True(funcStart >= 0);
        Assert.True(funcEnd > funcStart);

        var innerJson = content[(funcStart + "<function>".Length)..funcEnd];

        // Should be valid JSON
        using var doc = JsonDocument.Parse(innerJson);
        var root = doc.RootElement;

        Assert.Equal("mcp__slack__send", root.GetProperty("name").GetString());
        Assert.Equal("Send a slack message", root.GetProperty("description").GetString());

        // Parameters should round-trip to the tool's schema
        var parameters = root.GetProperty("parameters");
        Assert.Equal(JsonValueKind.Object, parameters.ValueKind);
        Assert.True(parameters.TryGetProperty("type", out _));
        Assert.True(parameters.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task OnToolsDiscovered_invoked_with_matches()
    {
        var toolA = new StubTool("mcp__slack__send", "Send a slack message", shouldDefer: true);
        var toolB = new StubTool("mcp__slack__list", "List slack channels", shouldDefer: true);
        var nonDeferred = new StubTool("ReadFile", shouldDefer: false);

        IReadOnlyList<ITool> allTools = [toolA, toolB, nonDeferred, this.tool];

        var captured = new List<IReadOnlyList<string>>();
        var context = MakeContext(allTools, captured);
        var input = MakeInput("select:mcp__slack__send,mcp__slack__list");

        await this.tool.ExecuteAsync(input, context);

        Assert.Single(captured);
        Assert.Equal(2, captured[0].Count);
        Assert.Contains("mcp__slack__send", captured[0]);
        Assert.Contains("mcp__slack__list", captured[0]);
    }

    [Fact]
    public async Task Empty_query_string_is_error()
    {
        IReadOnlyList<ITool> allTools = [this.tool];
        var captured = new List<IReadOnlyList<string>>();
        var context = MakeContext(allTools, captured);
        var input = MakeInput("");

        var result = await this.tool.ExecuteAsync(input, context);

        Assert.True(result.IsError);
        Assert.Contains("query is required", result.Content);
    }

    [Fact]
    public async Task AllTools_null_returns_no_match()
    {
        var context = new ToolContext("/tmp");
        var input = MakeInput("select:anything");

        var result = await this.tool.ExecuteAsync(input, context);

        Assert.False(result.IsError);
        Assert.Equal("No matching deferred tools found.", result.Content);
    }
}
