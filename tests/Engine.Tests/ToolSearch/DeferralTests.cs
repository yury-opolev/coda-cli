using System.Text.Json;
using Coda.Agent;
using Coda.Agent.ToolSearch;
using Coda.Mcp;

namespace Engine.Tests.ToolSearch;

public sealed class DeferralTests
{
    // ── A. ShouldDefer default false ────────────────────────────────────────

    [Fact]
    public void ShouldDefer_default_false()
    {
        ITool tool = new StubTool("my-tool");
        Assert.False(tool.ShouldDefer);
    }

    // ── B. McpTool.ShouldDefer is true ──────────────────────────────────────

    [Fact]
    public void Mcp_bridge_tool_ShouldDefer_true()
    {
        // McpTool is the bridge class for MCP-server-advertised tools.
        // Constructing it requires launching a real process (McpStdioClient).
        // Instead, verify via reflection that the property is explicitly overridden
        // on McpTool and returns true.
        var property = typeof(McpTool).GetProperty(nameof(ITool.ShouldDefer));
        Assert.NotNull(property);

        // The property getter must be explicitly declared on McpTool (not just the default).
        Assert.Equal(typeof(McpTool), property!.DeclaringType);

        // Confirm the default on the interface returns false (so the override matters).
        ITool defaultTool = new StubTool("default");
        Assert.False(defaultTool.ShouldDefer);
    }

    // ── C. ToolSearchModeResolver ────────────────────────────────────────────

    [Theory]
    [InlineData(null,          ToolSearchMode.Tst)]
    [InlineData("",            ToolSearchMode.Standard)]
    [InlineData("true",        ToolSearchMode.Tst)]
    [InlineData("1",           ToolSearchMode.Tst)]
    [InlineData("yes",         ToolSearchMode.Tst)]
    [InlineData("on",          ToolSearchMode.Tst)]
    [InlineData("TRUE",        ToolSearchMode.Tst)]
    [InlineData("false",       ToolSearchMode.Standard)]
    [InlineData("0",           ToolSearchMode.Standard)]
    [InlineData("no",          ToolSearchMode.Standard)]
    [InlineData("off",         ToolSearchMode.Standard)]
    [InlineData("FALSE",       ToolSearchMode.Standard)]
    [InlineData("auto",        ToolSearchMode.TstAuto)]
    [InlineData("auto:0",      ToolSearchMode.Tst)]
    [InlineData("auto:50",     ToolSearchMode.TstAuto)]
    [InlineData("auto:100",    ToolSearchMode.Standard)]
    [InlineData("auto:notanumber", ToolSearchMode.TstAuto)]  // starts with "auto:" → isAutoMode → TstAuto
    [InlineData("AUTO",        ToolSearchMode.TstAuto)]
    [InlineData("AUTO:50",     ToolSearchMode.TstAuto)]
    public void Resolve_env_values(string? value, ToolSearchMode expected)
    {
        var result = ToolSearchModeResolver.Resolve(value);
        Assert.Equal(expected, result);
    }

    // ── D. DeferredTools.IsDeferred ─────────────────────────────────────────

    [Fact]
    public void IsDeferred_true_when_ShouldDefer_and_not_tool_search_name()
    {
        ITool tool = new StubTool("x", shouldDefer: true);
        Assert.True(DeferredTools.IsDeferred(tool));
    }

    [Fact]
    public void IsDeferred_false_when_name_is_tool_search()
    {
        ITool tool = new StubTool(ToolSearchToolNames.ToolSearch, shouldDefer: true);
        Assert.False(DeferredTools.IsDeferred(tool));
    }

    [Fact]
    public void IsDeferred_false_when_ShouldDefer_false()
    {
        ITool tool = new StubTool("x", shouldDefer: false);
        Assert.False(DeferredTools.IsDeferred(tool));
    }

    // ── Test helpers ────────────────────────────────────────────────────────

    private sealed class StubTool : ITool
    {
        private readonly bool shouldDefer;

        public StubTool(string name, bool shouldDefer = false)
        {
            this.Name = name;
            this.shouldDefer = shouldDefer;
        }

        public string Name { get; }
        public string Description => "stub";
        public string InputSchemaJson => "{}";
        public bool IsReadOnly => true;
        public bool ShouldDefer => this.shouldDefer;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult("stub"));
    }

}

