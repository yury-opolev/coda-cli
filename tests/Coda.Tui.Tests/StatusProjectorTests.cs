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
            "gpt-5.6-sol | perm ask !1 | high | ctx 42%",
            StatusProjector.Project(snapshot, 44));
    }

    [Theory]
    [InlineData(PermissionMode.Default, "perm ask")]
    [InlineData(PermissionMode.AcceptEdits, "perm edits")]
    [InlineData(PermissionMode.Plan, "perm plan")]
    [InlineData(PermissionMode.BypassPermissions, "perm yolo")]
    public void Permission_mode_maps_to_compact_label(PermissionMode mode, string expected)
    {
        var snapshot = Wide() with { Permission = new PermissionStatus(mode, 0) };

        var line = StatusProjector.Project(snapshot, 200);

        Assert.Contains(expected, line, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_field_order_is_model_permission_effort()
    {
        var line = StatusProjector.Project(Wide(), 200);

        var model = line.IndexOf("gpt-5.6-sol", StringComparison.Ordinal);
        var permission = line.IndexOf("perm ask", StringComparison.Ordinal);
        var effort = line.IndexOf("high", StringComparison.Ordinal);

        Assert.True(model >= 0 && permission >= 0 && effort >= 0);
        Assert.True(model < permission, "model must precede permission");
        Assert.True(permission < effort, "permission must precede effort");
    }

    [Fact]
    public void Low_priority_fields_are_shed_before_permission()
    {
        var line = StatusProjector.Project(Wide(), 60);

        Assert.Contains("perm ask", line, StringComparison.Ordinal);
        Assert.DoesNotContain("MCP", line, StringComparison.Ordinal);
        Assert.DoesNotContain("main", line, StringComparison.Ordinal);
        Assert.True(line.Length <= 60);
    }

    [Fact]
    public void Narrow_width_keeps_permission_and_sheds_effort_when_only_model_and_permission_fit()
    {
        var snapshot = Wide() with { Permission = new PermissionStatus(PermissionMode.Default, 0) };

        var line = StatusProjector.Project(snapshot, 25);

        Assert.Equal("gpt-5.6-sol | perm ask", line);
    }

    [Fact]
    public void Pending_count_is_appended_after_current_mode()
    {
        var snapshot = Wide() with { Permission = new PermissionStatus(PermissionMode.Default, 3) };

        var line = StatusProjector.Project(snapshot, 200);

        Assert.Contains("perm ask !3", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Zero_pending_shows_only_current_mode()
    {
        var snapshot = Wide() with { Permission = new PermissionStatus(PermissionMode.BypassPermissions, 0) };

        var line = StatusProjector.Project(snapshot, 200);

        Assert.Contains("perm yolo", line, StringComparison.Ordinal);
        Assert.DoesNotContain("!", line, StringComparison.Ordinal);
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
    public void Medium_width_66_keeps_usage_but_drops_services_and_git()
    {
        var line = StatusProjector.Project(Wide(), 66);

        Assert.Contains("perm ask", line, StringComparison.Ordinal);
        Assert.Contains("18.2k in / 2.4k out", line);
        Assert.DoesNotContain("MCP", line);
        Assert.DoesNotContain("main", line);
        Assert.True(line.Length <= 66);
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
