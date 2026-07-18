using Coda.Agent;
using Coda.Tui.Ui.State;
using LlmClient;

namespace Coda.Tui.Tests;

/// <summary>
/// Verifies <see cref="StatusProjector.Project"/>: the responsive, single-line status bar that
/// renders the ordered runtime fields and sheds them from the end (finally truncating the model
/// itself) so the result always fits the available terminal width.
/// </summary>
public sealed class StatusProjectorTests
{
    private static UiSessionSnapshot Wide() => UiSessionSnapshot.Empty with
    {
        Model = "gpt-5.6-sol",
        EffectiveEffort = "high",
        Context = new ContextStatus(UsedTokens: 84_000, MaxTokens: 200_000, Percentage: 42, IsExact: true),
        Permission = new PermissionStatus(PermissionMode.Default, 0),
        SessionUsage = new TokenUsage(18_200, 2_400),
        EstimatedCost = 0.184m,
        Mcp = new ServiceStatus(3, 0),
        Lsp = new ServiceStatus(2, 0),
        Git = new GitStatus("main", Dirty: true),
        WorkingDirectory = "/repo",
    };

    [Fact]
    public void Narrow_width_44_renders_only_stable_metadata_prefix()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            Model = "gpt-5.6-sol",
            EffectiveEffort = "high",
            Context = new ContextStatus(84_000, 200_000, 42, true),
            Permission = new PermissionStatus(PermissionMode.Default, 1),
            ActiveOperation = new ActiveOperation("tool", "running tool", null),
            WorkingDirectory = string.Empty,
        };

        Assert.Equal(
            "gpt-5.6-sol | high | ctx 42%",
            StatusProjector.Project(snapshot, 44));
    }

    [Fact]
    public void Metadata_never_contains_active_operation_label()
    {
        var snapshot = Wide() with
        {
            ActiveOperation = new ActiveOperation("tool", "running secret tool label", null),
        };

        var line = StatusProjector.Project(snapshot, width: 200);

        Assert.DoesNotContain("running secret tool label", line, StringComparison.Ordinal);
        Assert.DoesNotContain("permission", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("default", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gpt-5.6-sol", line, StringComparison.Ordinal);
        Assert.Contains("ctx 84k/200k", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Wide_width_160_renders_full_status()
    {
        var line = StatusProjector.Project(Wide(), 160);

        Assert.Contains("ctx 84k/200k", line);
        Assert.Contains("18.2k in / 2.4k out", line);
        Assert.Contains("$0.184", line);
        Assert.Contains("MCP 3", line);
        Assert.Contains("LSP 2", line);
        Assert.Contains("main*", line);
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public void Medium_width_60_keeps_usage_but_drops_services_and_git()
    {
        var line = StatusProjector.Project(Wide(), 60);

        Assert.Contains("18.2k in / 2.4k out", line);
        Assert.DoesNotContain("MCP", line);
        Assert.DoesNotContain("main", line);
        Assert.True(line.Length <= 60);
    }

    [Fact]
    public void Estimated_context_tokens_are_prefixed_with_tilde()
    {
        var snapshot = Wide() with
        {
            Context = new ContextStatus(UsedTokens: 84_000, MaxTokens: 200_000, Percentage: 42, IsExact: false),
        };

        var line = StatusProjector.Project(snapshot, 120);

        Assert.Contains("ctx ~84k/200k", line);
    }

    [Fact]
    public void Model_wider_than_width_is_truncated_with_ellipsis()
    {
        var snapshot = UiSessionSnapshot.Empty with { Model = "gpt-5.6-sol" };

        var line = StatusProjector.Project(snapshot, 6);

        Assert.Equal("gpt-5\u2026", line);
    }
}
