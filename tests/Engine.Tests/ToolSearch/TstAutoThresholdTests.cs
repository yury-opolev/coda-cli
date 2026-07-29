using System.Text.Json;
using Coda.Agent;
using Coda.Agent.ToolSearch;
using LlmClient;

namespace Engine.Tests.ToolSearch;

/// <summary>
/// Tests for the TstAuto threshold gate and ResolveAutoPercentage.
/// </summary>
public sealed class TstAutoThresholdTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a deferred stub tool with controlled name/description/schema sizes.
    /// </summary>
    private static ITool MakeDeferredTool(
        string name,
        string description = "stub description",
        string inputSchemaJson = "{}") =>
        new StubTool(name, description, inputSchemaJson, shouldDefer: true);

    private static ITool MakeNonDeferredTool(string name) =>
        new StubTool(name, "stub", "{}", shouldDefer: false);

    private static ToolRegistry MakeRegistryWithOneDeferredTool(
        string deferredName = "mcp__x__tool",
        string description = "d",
        string inputSchemaJson = "{}") =>
        new([
            MakeNonDeferredTool("tool_search"),
            MakeNonDeferredTool("read_file"),
            MakeDeferredTool(deferredName, description, inputSchemaJson),
        ]);

    // ── A. ResolveAutoPercentage ──────────────────────────────────────────────

    [Theory]
    [InlineData("auto",      10)]   // plain auto → default 10
    [InlineData("auto:50",   50)]   // explicit numeric
    [InlineData("auto:150", 100)]   // clamped to 100
    [InlineData("auto:bad",  10)]   // invalid N → default 10
    [InlineData(null,         10)]   // unset → default 10
    [InlineData("auto:0",     0)]   // 0 is valid
    [InlineData("auto:100", 100)]   // 100 is valid
    public void ResolveAutoPercentage_returns_expected(string? env, int expected)
    {
        var result = ToolSearchModeResolver.ResolveAutoPercentage(env);
        Assert.Equal(expected, result);
    }

    // ── B. TstAuto below threshold → all inline ───────────────────────────────

    [Fact]
    public void TstAuto_below_threshold_keeps_all_inline()
    {
        // deferred tool: name="mcp__x__tool" (12 chars), description="d" (1 char), schema="{}" (2 chars)
        // total deferred chars = 15
        // contextWindowTokens=200000, autoPercent=10
        // threshold = floor(200000 * (10/100.0) * 2.5) = floor(50000) = 50000
        // 15 < 50000 → ShouldDeferNow = false → all inline
        var registry = MakeRegistryWithOneDeferredTool(
            deferredName: "mcp__x__tool",
            description: "d",
            inputSchemaJson: "{}");
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.TstAuto, autoPercent: 10, contextWindowTokens: 200_000);

        var wire = coordinator.BuildWireDefinitions(registry);
        var wireNames = wire.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        // The deferred tool should be inline (not excluded)
        Assert.Contains("mcp__x__tool", wireNames);
        Assert.Contains("read_file", wireNames);
        Assert.Contains("tool_search", wireNames);

        var reminder = coordinator.BuildDeferredToolsReminder(registry);
        Assert.Null(reminder);
    }

    // ── C. TstAuto above threshold → defers ──────────────────────────────────

    [Fact]
    public void TstAuto_above_threshold_defers()
    {
        // contextWindowTokens=100, autoPercent=1
        // threshold = floor(100 * (1/100.0) * 2.5) = floor(2.5) = 2 chars
        // deferred tool: "mcp__x__tool" (12) + "d" (1) + "{}" (2) = 15 chars > 2
        // ShouldDeferNow = true → deferred tool excluded; tool_search included; reminder lists it
        var registry = MakeRegistryWithOneDeferredTool(
            deferredName: "mcp__x__tool",
            description: "d",
            inputSchemaJson: "{}");
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.TstAuto, autoPercent: 1, contextWindowTokens: 100);

        var wire = coordinator.BuildWireDefinitions(registry);
        var wireNames = wire.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        // Deferred tool should be excluded from wire
        Assert.DoesNotContain("mcp__x__tool", wireNames);
        Assert.Contains("tool_search", wireNames);
        Assert.Contains("read_file", wireNames);

        var reminder = coordinator.BuildDeferredToolsReminder(registry);
        Assert.NotNull(reminder);
        Assert.Contains("mcp__x__tool", reminder);
    }

    // ── D. Tst always defers regardless of size ───────────────────────────────

    [Fact]
    public void Tst_always_defers_regardless_of_size()
    {
        // Even with a large contextWindowTokens (low threshold relative to tool),
        // Tst mode always defers — threshold gate is only for TstAuto
        var registry = MakeRegistryWithOneDeferredTool(
            deferredName: "mcp__x__tool",
            description: "d",
            inputSchemaJson: "{}");
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst, autoPercent: 10, contextWindowTokens: 200_000);

        var wire = coordinator.BuildWireDefinitions(registry);
        var wireNames = wire.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("mcp__x__tool", wireNames);
        Assert.Contains("tool_search", wireNames);

        var reminder = coordinator.BuildDeferredToolsReminder(registry);
        Assert.NotNull(reminder);
        Assert.Contains("mcp__x__tool", reminder);
    }

    // ── E. Standard never defers ──────────────────────────────────────────────

    [Fact]
    public void Standard_never_defers()
    {
        var registry = MakeRegistryWithOneDeferredTool();
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Standard, autoPercent: 10, contextWindowTokens: 100);

        var wire = coordinator.BuildWireDefinitions(registry);
        Assert.Equal(registry.Definitions.Count, wire.Count);

        var reminder = coordinator.BuildDeferredToolsReminder(registry);
        Assert.Null(reminder);
    }

    // ── F. TstAuto at exact threshold boundary (equal → defers) ──────────────

    [Fact]
    public void TstAuto_at_exact_threshold_boundary_defers()
    {
        // "mcp__x__tool" = 12 chars, "d" = 1 char, "{}" = 2 chars → total = 15
        // We want threshold == 15 exactly.
        // threshold = floor(contextWindowTokens * (autoPercent/100.0) * 2.5)
        // 15 = floor(T * 0.10 * 2.5) = floor(T * 0.25)
        // T = 60 → floor(60 * 0.25) = floor(15) = 15 ✓
        var registry = MakeRegistryWithOneDeferredTool(
            deferredName: "mcp__x__tool",
            description: "d",
            inputSchemaJson: "{}");
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.TstAuto, autoPercent: 10, contextWindowTokens: 60);

        var wire = coordinator.BuildWireDefinitions(registry);
        var wireNames = wire.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        // 15 >= 15 → should defer
        Assert.DoesNotContain("mcp__x__tool", wireNames);
        var reminder = coordinator.BuildDeferredToolsReminder(registry);
        Assert.NotNull(reminder);
    }

    // ── G. Always-inline tools count towards the footprint ───────────────────

    [Fact]
    public void TstAuto_counts_always_inline_tool_definitions_in_the_budget()
    {
        // The skill tool is never deferred, yet its catalogue description can reach ~8 000
        // characters of context. The TstAuto trigger measures the whole tool-definition
        // footprint, so a large always-inline tool pushes a small deferred set over the line.
        //
        // threshold = floor(400 * (10/100.0) * 2.5) = 100 chars
        // deferred alone: "mcp__x__tool"(12) + "d"(1) + "{}"(2) = 15  → below on its own
        // plus always-inline: tool_search(17) + read_file(15) + skill catalogue(> 100) → above
        var registry = new ToolRegistry([
            MakeNonDeferredTool("tool_search"),
            MakeNonDeferredTool("read_file"),
            new StubTool("skill", new string('x', 200), "{}", shouldDefer: false),
            MakeDeferredTool("mcp__x__tool", "d", "{}"),
        ]);

        var coordinator = new ToolSearchCoordinator(
            ToolSearchMode.TstAuto, autoPercent: 10, contextWindowTokens: 400);

        var wireNames = coordinator.BuildWireDefinitions(registry)
            .Select(d => d.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("mcp__x__tool", wireNames);
        Assert.Contains("skill", wireNames);
        Assert.Contains("tool_search", wireNames);

        var reminder = coordinator.BuildDeferredToolsReminder(registry);
        Assert.NotNull(reminder);
        Assert.Contains("mcp__x__tool", reminder);
    }

    [Fact]
    public void TstAuto_stays_inline_when_the_whole_footprint_is_small()
    {
        // Same registry shape without the oversized always-inline description: the total
        // footprint stays under the threshold, so nothing is deferred.
        var registry = new ToolRegistry([
            MakeNonDeferredTool("tool_search"),
            MakeNonDeferredTool("read_file"),
            new StubTool("skill", "small catalogue", "{}", shouldDefer: false),
            MakeDeferredTool("mcp__x__tool", "d", "{}"),
        ]);

        var coordinator = new ToolSearchCoordinator(
            ToolSearchMode.TstAuto, autoPercent: 10, contextWindowTokens: 400);

        var wireNames = coordinator.BuildWireDefinitions(registry)
            .Select(d => d.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("mcp__x__tool", wireNames);
        Assert.Null(coordinator.BuildDeferredToolsReminder(registry));
    }

    // ── test helpers ─────────────────────────────────────────────────────────

    private sealed class StubTool : ITool
    {
        private readonly bool shouldDefer;

        public StubTool(string name, string description, string inputSchemaJson, bool shouldDefer)
        {
            this.Name = name;
            this.Description = description;
            this.InputSchemaJson = inputSchemaJson;
            this.shouldDefer = shouldDefer;
        }

        public string Name { get; }
        public string Description { get; }
        public string InputSchemaJson { get; }
        public bool IsReadOnly => true;
        public bool ShouldDefer => this.shouldDefer;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult("stub"));
    }
}
