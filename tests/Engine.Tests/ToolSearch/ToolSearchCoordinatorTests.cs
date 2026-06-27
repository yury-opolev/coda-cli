using System.Text.Json;
using Coda.Agent;
using Coda.Agent.ToolSearch;
using LlmClient;

namespace Engine.Tests.ToolSearch;

public sealed class ToolSearchCoordinatorTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    private static ITool MakeTool(string name, bool shouldDefer = false) => new StubTool(name, shouldDefer);

    private static ToolRegistry MakeRegistry() =>
        new([
            MakeTool("read_file",    shouldDefer: false),
            MakeTool("mcp__a__x",   shouldDefer: true),
            MakeTool("mcp__b__y",   shouldDefer: true),
            MakeTool("tool_search", shouldDefer: false),
        ]);

    // ── A. IsActive ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ToolSearchMode.Standard, false)]
    [InlineData(ToolSearchMode.Tst,      true)]
    [InlineData(ToolSearchMode.TstAuto,  true)]
    public void IsActive(ToolSearchMode mode, bool expected)
    {
        var coordinator = new ToolSearchCoordinator(mode);
        Assert.Equal(expected, coordinator.IsActive);
    }

    // ── B. Standard mode ────────────────────────────────────────────────────

    [Fact]
    public void Standard_mode_wire_is_all_and_reminder_null()
    {
        var registry = MakeRegistry();
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Standard);

        var wire = coordinator.BuildWireDefinitions(registry);
        var reminder = coordinator.BuildDeferredToolsReminder(registry);

        Assert.Equal(registry.Definitions.Count, wire.Count);
        Assert.Null(reminder);
    }

    // ── C. Tst mode excludes deferred, includes tool_search ─────────────────

    [Fact]
    public void Tst_mode_excludes_deferred_includes_tool_search()
    {
        var registry = MakeRegistry();
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);

        var wire = coordinator.BuildWireDefinitions(registry);
        var wireNames = wire.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("read_file",    wireNames);
        Assert.Contains("tool_search",  wireNames);
        Assert.DoesNotContain("mcp__a__x", wireNames);
        Assert.DoesNotContain("mcp__b__y", wireNames);

        var reminder = coordinator.BuildDeferredToolsReminder(registry);
        Assert.NotNull(reminder);
        Assert.Contains("mcp__a__x", reminder);
        Assert.Contains("mcp__b__y", reminder);
    }

    // ── D. After AddDiscovered includes that tool ────────────────────────────

    [Fact]
    public void After_AddDiscovered_includes_that_tool()
    {
        var registry = MakeRegistry();
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);

        coordinator.AddDiscovered(["mcp__a__x"]);

        var wire = coordinator.BuildWireDefinitions(registry);
        var wireNames = wire.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("read_file",   wireNames);
        Assert.Contains("tool_search", wireNames);
        Assert.Contains("mcp__a__x",   wireNames);
        Assert.DoesNotContain("mcp__b__y", wireNames);

        var reminder = coordinator.BuildDeferredToolsReminder(registry);
        Assert.NotNull(reminder);
        Assert.DoesNotContain("mcp__a__x", reminder);
        Assert.Contains("mcp__b__y", reminder);
    }

    // ── E. All discovered → reminder null; wire includes all ────────────────

    [Fact]
    public void All_discovered_reminder_null()
    {
        var registry = MakeRegistry();
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);

        coordinator.AddDiscovered(["mcp__a__x", "mcp__b__y"]);

        var wire = coordinator.BuildWireDefinitions(registry);
        Assert.Equal(registry.All.Count, wire.Count);

        var reminder = coordinator.BuildDeferredToolsReminder(registry);
        Assert.Null(reminder);
    }

    // ── F. Reminder lists names in registry order ────────────────────────────

    [Fact]
    public void Reminder_lists_names_in_registry_order()
    {
        // Registry order: read_file, mcp__a__x, mcp__b__y, tool_search
        // Deferred not-yet-discovered: mcp__a__x, mcp__b__y — in that order
        var registry = MakeRegistry();
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);

        var reminder = coordinator.BuildDeferredToolsReminder(registry);
        Assert.NotNull(reminder);

        var posA = reminder!.IndexOf("mcp__a__x", StringComparison.Ordinal);
        var posB = reminder.IndexOf("mcp__b__y", StringComparison.Ordinal);
        Assert.True(posA < posB, "mcp__a__x must appear before mcp__b__y in reminder");
    }

    // ── G. Reminder format ───────────────────────────────────────────────────

    [Fact]
    public void Reminder_contains_expected_tags_and_instruction()
    {
        var registry = MakeRegistry();
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);

        var reminder = coordinator.BuildDeferredToolsReminder(registry);
        Assert.NotNull(reminder);
        Assert.StartsWith("<deferred-tools>", reminder);
        Assert.EndsWith("</deferred-tools>", reminder);
        Assert.Contains("tool_search", reminder);
    }

    // ── test helpers ─────────────────────────────────────────────────────────

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
