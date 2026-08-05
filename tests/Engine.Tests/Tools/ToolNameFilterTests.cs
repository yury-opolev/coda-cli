using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;

namespace Engine.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="ToolNameFilter"/>: allow/deny application, deny-wins-on-conflict,
/// case-insensitivity, ExtraTools (MCP/plugin) filtering, and user/project merge semantics.
/// </summary>
public sealed class ToolNameFilterTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ITool Stub(string name) => new StubTool(name);

    private sealed class StubTool(string name) : ITool
    {
        public string Name => name;
        public string Description => name;
        public string InputSchemaJson => "{}";
        public bool IsReadOnly => true;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult("ok"));
    }

    // ------------------------------------------------------------------
    // Apply: allow
    // ------------------------------------------------------------------

    [Fact]
    public void Allow_null_passes_all_tools()
    {
        var filter = new ToolNameFilter(allow: null, deny: []);
        var tools = new[] { Stub("read_file"), Stub("run_command"), Stub("task") };

        var result = filter.Apply(tools).Select(t => t.Name).ToArray();

        Assert.Equal(["read_file", "run_command", "task"], result);
    }

    [Fact]
    public void Allow_list_restricts_to_named_tools()
    {
        var filter = new ToolNameFilter(allow: ["task", "task_start"], deny: []);
        var tools = new[] { Stub("read_file"), Stub("run_command"), Stub("task"), Stub("task_start") };

        var result = filter.Apply(tools).Select(t => t.Name).ToArray();

        Assert.Equal(["task", "task_start"], result);
    }

    [Fact]
    public void Allow_empty_array_is_honoured_literally_and_blocks_everything()
    {
        var filter = new ToolNameFilter(allow: [], deny: []);
        var tools = new[] { Stub("read_file"), Stub("task") };

        var result = filter.Apply(tools).ToArray();

        Assert.Empty(result);
    }

    // ------------------------------------------------------------------
    // Apply: deny
    // ------------------------------------------------------------------

    [Fact]
    public void Deny_removes_named_tools_when_no_allowlist()
    {
        var filter = new ToolNameFilter(allow: null, deny: ["run_command"]);
        var tools = new[] { Stub("read_file"), Stub("run_command"), Stub("task") };

        var result = filter.Apply(tools).Select(t => t.Name).ToArray();

        Assert.Equal(["read_file", "task"], result);
    }

    [Fact]
    public void Deny_wins_when_name_is_in_both_allow_and_deny()
    {
        var filter = new ToolNameFilter(allow: ["task", "run_command"], deny: ["run_command"]);
        var tools = new[] { Stub("task"), Stub("run_command"), Stub("read_file") };

        var result = filter.Apply(tools).Select(t => t.Name).ToArray();

        Assert.Equal(["task"], result);
    }

    // ------------------------------------------------------------------
    // Case insensitivity
    // ------------------------------------------------------------------

    [Fact]
    public void Allow_is_case_insensitive()
    {
        var filter = new ToolNameFilter(allow: ["TASK", "Task_Start"], deny: []);
        var tools = new[] { Stub("task"), Stub("task_start"), Stub("read_file") };

        var result = filter.Apply(tools).Select(t => t.Name).ToArray();

        Assert.Equal(["task", "task_start"], result);
    }

    [Fact]
    public void Deny_is_case_insensitive()
    {
        var filter = new ToolNameFilter(allow: null, deny: ["RUN_COMMAND"]);
        var tools = new[] { Stub("run_command"), Stub("task") };

        var result = filter.Apply(tools).Select(t => t.Name).ToArray();

        Assert.Equal(["task"], result);
    }

    // ------------------------------------------------------------------
    // ExtraTools (MCP/plugin) — same rule applies
    // ------------------------------------------------------------------

    [Fact]
    public void Extra_tools_are_filtered_by_the_same_allow_rule()
    {
        // Simulates MCP tool named "mcp__github__get_file" included in ExtraTools.
        var filter = new ToolNameFilter(allow: ["task", "task_start"], deny: []);
        var tools = new[] { Stub("task"), Stub("task_start"), Stub("mcp__github__get_file") };

        var result = filter.Apply(tools).Select(t => t.Name).ToArray();

        // MCP tool is not in the allowlist → filtered out.
        Assert.Equal(["task", "task_start"], result);
    }

    [Fact]
    public void Extra_tools_can_be_explicitly_allowed()
    {
        var filter = new ToolNameFilter(allow: ["task", "task_start", "mcp__github__get_file"], deny: []);
        var tools = new[] { Stub("task"), Stub("task_start"), Stub("mcp__github__get_file"), Stub("read_file") };

        var result = filter.Apply(tools).Select(t => t.Name).ToArray();

        Assert.Equal(["task", "task_start", "mcp__github__get_file"], result);
    }

    // ------------------------------------------------------------------
    // Unknown names are inert
    // ------------------------------------------------------------------

    [Fact]
    public void Unknown_allow_names_are_inert_and_do_not_error()
    {
        var filter = new ToolNameFilter(allow: ["task", "does_not_exist"], deny: []);
        var tools = new[] { Stub("task"), Stub("read_file") };

        var result = filter.Apply(tools).Select(t => t.Name).ToArray();

        // "does_not_exist" does not cause an error — it is simply never matched.
        Assert.Equal(["task"], result);
    }

    [Fact]
    public void Unknown_deny_names_are_inert_and_do_not_error()
    {
        var filter = new ToolNameFilter(allow: null, deny: ["does_not_exist"]);
        var tools = new[] { Stub("task"), Stub("read_file") };

        var result = filter.Apply(tools).Select(t => t.Name).ToArray();

        Assert.Equal(["task", "read_file"], result);
    }

    // ------------------------------------------------------------------
    // Passes helper
    // ------------------------------------------------------------------

    [Fact]
    public void Passes_returns_true_when_no_filter_configured()
    {
        var filter = new ToolNameFilter(allow: null, deny: []);

        Assert.True(filter.Passes("anything"));
    }

    [Fact]
    public void Passes_returns_false_for_name_excluded_by_allow()
    {
        var filter = new ToolNameFilter(allow: ["task"], deny: []);

        Assert.False(filter.Passes("read_file"));
    }

    [Fact]
    public void Passes_returns_false_for_name_excluded_by_deny()
    {
        var filter = new ToolNameFilter(allow: null, deny: ["run_command"]);

        Assert.False(filter.Passes("run_command"));
    }

    // ------------------------------------------------------------------
    // Merge: allow intersected
    // ------------------------------------------------------------------

    [Fact]
    public void Merge_allow_is_intersected()
    {
        var user = new ToolNameFilter(allow: ["task", "task_start", "read_file"], deny: []);
        var project = new ToolNameFilter(allow: ["task", "task_start"], deny: []);

        var merged = ToolNameFilter.Merge(user, project);

        Assert.NotNull(merged.Allow);
        var allow = new HashSet<string>(merged.Allow!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("task", allow!);
        Assert.Contains("task_start", allow!);
        Assert.DoesNotContain("read_file", allow!); // narrowed by project
    }

    [Fact]
    public void Project_cannot_widen_user_allowlist()
    {
        // User restricts to task only; project tries to add read_file — it must not appear.
        var user = new ToolNameFilter(allow: ["task"], deny: []);
        var project = new ToolNameFilter(allow: ["task", "read_file"], deny: []);

        var merged = ToolNameFilter.Merge(user, project);

        Assert.NotNull(merged.Allow);
        Assert.DoesNotContain("read_file", merged.Allow!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Merge_allow_null_from_one_side_uses_other()
    {
        // Only project has an allowlist → that list is used as-is.
        var user = new ToolNameFilter(allow: null, deny: []);
        var project = new ToolNameFilter(allow: ["task", "task_start"], deny: []);

        var merged = ToolNameFilter.Merge(user, project);

        Assert.NotNull(merged.Allow);
        Assert.Equal(2, merged.Allow!.Count);
    }

    [Fact]
    public void Merge_allow_null_from_both_sides_stays_null()
    {
        var user = new ToolNameFilter(allow: null, deny: []);
        var project = new ToolNameFilter(allow: null, deny: []);

        var merged = ToolNameFilter.Merge(user, project);

        Assert.Null(merged.Allow);
    }

    // ------------------------------------------------------------------
    // Merge: deny unioned
    // ------------------------------------------------------------------

    [Fact]
    public void Merge_deny_is_unioned()
    {
        var user = new ToolNameFilter(allow: null, deny: ["run_command"]);
        var project = new ToolNameFilter(allow: null, deny: ["write_file"]);

        var merged = ToolNameFilter.Merge(user, project);

        Assert.Contains("run_command", merged.Deny, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("write_file", merged.Deny, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Merge_deny_deduplicates_case_insensitively()
    {
        var user = new ToolNameFilter(allow: null, deny: ["RUN_COMMAND"]);
        var project = new ToolNameFilter(allow: null, deny: ["run_command"]);

        var merged = ToolNameFilter.Merge(user, project);

        Assert.Single(merged.Deny);
    }

    // ------------------------------------------------------------------
    // Merge: both null → identity result
    // ------------------------------------------------------------------

    [Fact]
    public void Merge_both_null_returns_no_op_filter()
    {
        var merged = ToolNameFilter.Merge(null, null);

        Assert.Null(merged.Allow);
        Assert.Empty(merged.Deny);
    }
}
