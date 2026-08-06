using Coda.Agent;
using Coda.Agent.ToolSearch;
using LlmClient;

namespace Engine.Tests.ToolSearch;

/// <summary>
/// Recovery: a tool definition the model API rejects must cost that tool, not the session.
/// Covers the pure pieces — parsing the rejection, the quarantine set, and making tool
/// discovery reversible.
/// </summary>
public sealed class ToolSchemaRecoveryTests
{
    private static IReadOnlyList<ToolDefinition> Definitions(params string[] names) =>
        [.. names.Select(n => new ToolDefinition(n, "d", """{"type":"object"}"""))];

    // ── identifying the offending tool ──────────────────────────────────────

    [Fact]
    public void Anthropic_index_form_identifies_the_tool()
    {
        var defs = Definitions("a", "b", "c");

        Assert.True(ToolSchemaRejection.TryIdentify(
            "Model API request failed (HTTP 400): tools.1.custom.input_schema.type: Field required",
            defs,
            out var name));
        Assert.Equal("b", name);
    }

    [Fact]
    public void OpenAi_bracket_index_form_identifies_the_tool()
    {
        var defs = Definitions("a", "b", "c");

        Assert.True(ToolSchemaRejection.TryIdentify(
            "Invalid 'tools[2].function.parameters': schema must be an object",
            defs,
            out var name));
        Assert.Equal("c", name);
    }

    [Fact]
    public void OpenAi_named_function_form_identifies_the_tool()
    {
        var defs = Definitions("a", "mcp__playwright__browser_navigate");

        Assert.True(ToolSchemaRejection.TryIdentify(
            "Invalid schema for function 'mcp__playwright__browser_navigate': schema must have a 'type'.",
            defs,
            out var name));
        Assert.Equal("mcp__playwright__browser_navigate", name);
    }

    [Fact]
    public void An_out_of_range_index_is_not_identified()
    {
        // Never evict by guesswork: a mismatched index means we cannot attribute the failure.
        Assert.False(ToolSchemaRejection.TryIdentify(
            "tools.29.custom.input_schema.type: Field required",
            Definitions("a", "b"),
            out _));
    }

    [Fact]
    public void A_named_function_absent_from_the_request_is_not_identified()
    {
        Assert.False(ToolSchemaRejection.TryIdentify(
            "Invalid schema for function 'not_sent': ...",
            Definitions("a"),
            out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Model API request failed (HTTP 400): messages.3: invalid role")]
    [InlineData("rate limit exceeded")]
    [InlineData("tools must be an array")]
    public void Unrelated_errors_are_not_identified(string message)
    {
        Assert.False(ToolSchemaRejection.TryIdentify(message, Definitions("a", "b"), out _));
    }

    [Fact]
    public void A_negative_or_absurd_index_is_not_identified()
    {
        Assert.False(ToolSchemaRejection.TryIdentify(
            "tools.999999999999999999999.custom.input_schema.type: Field required",
            Definitions("a"),
            out _));
    }

    [Fact]
    public void Only_schema_shaped_tool_errors_are_identified()
    {
        // A tool-related 400 that is not about the tool's definition must not evict a tool.
        Assert.False(ToolSchemaRejection.TryIdentify(
            "tool_use ids must be unique",
            Definitions("a", "b"),
            out _));
    }

    // ── the quarantine set ──────────────────────────────────────────────────

    [Fact]
    public void Quarantine_filters_the_named_tool_out_of_the_wire_definitions()
    {
        var quarantine = new ToolQuarantine();
        Assert.True(quarantine.Add("b"));

        var filtered = quarantine.Filter(Definitions("a", "b", "c"));

        Assert.Equal(["a", "c"], filtered.Select(d => d.Name));
    }

    [Fact]
    public void An_empty_quarantine_returns_the_same_instance()
    {
        var definitions = Definitions("a", "b");

        Assert.Same(definitions, new ToolQuarantine().Filter(definitions));
    }

    [Fact]
    public void Adding_the_same_tool_twice_reports_no_change()
    {
        var quarantine = new ToolQuarantine();

        Assert.True(quarantine.Add("a"));
        Assert.False(quarantine.Add("a"));
        Assert.Single(quarantine.Names);
    }

    // ── reversible discovery ────────────────────────────────────────────────

    private static ToolRegistry Registry() =>
        new([new StubTool("read_file", false), new StubTool("mcp__a__x", true), new StubTool("mcp__b__y", true)]);

    [Fact]
    public void A_discovered_tool_can_be_evicted_back_to_deferred()
    {
        var registry = Registry();
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);
        coordinator.AddDiscovered(["mcp__a__x"]);
        Assert.Contains("mcp__a__x", coordinator.BuildWireDefinitions(registry).Select(d => d.Name));

        Assert.True(coordinator.RemoveDiscovered("mcp__a__x"));

        Assert.DoesNotContain("mcp__a__x", coordinator.BuildWireDefinitions(registry).Select(d => d.Name));
        Assert.Contains("mcp__a__x", coordinator.BuildDeferredToolsReminder(registry));
    }

    [Fact]
    public void Evicting_an_undiscovered_tool_reports_no_change()
    {
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);

        Assert.False(coordinator.RemoveDiscovered("mcp__a__x"));
    }

    [Fact]
    public void Reset_clears_all_discovery_state()
    {
        var registry = Registry();
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);
        coordinator.AddDiscovered(["mcp__a__x", "mcp__b__y"]);

        coordinator.ResetDiscovered();

        Assert.Empty(coordinator.Discovered);
        var wire = coordinator.BuildWireDefinitions(registry).Select(d => d.Name).ToList();
        Assert.DoesNotContain("mcp__a__x", wire);
        Assert.DoesNotContain("mcp__b__y", wire);
    }

    [Fact]
    public void Discovered_exposes_the_current_set()
    {
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);
        coordinator.AddDiscovered(["mcp__a__x"]);

        Assert.Equal(["mcp__a__x"], coordinator.Discovered);
    }

    private sealed class StubTool(string name, bool shouldDefer) : ITool
    {
        public string Name => name;
        public string Description => "stub";
        public string InputSchemaJson => """{"type":"object"}""";
        public bool IsReadOnly => true;
        public bool ShouldDefer => shouldDefer;

        public Task<ToolResult> ExecuteAsync(
            System.Text.Json.JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult("stub"));
    }
}
