using Coda.Agent.Settings;
using Coda.Agent.Tools;

namespace Engine.Tests.Settings;

/// <summary>
/// Tests for the <c>agent.tools</c> settings block: parsing, absent-means-no-restriction,
/// empty-array-honoured-literally, unknown-names-inert, and user/project merge semantics.
/// Also covers the inert-agent guard that refuses a configuration where neither
/// <c>task</c> nor <c>task_start</c> would pass the filter.
/// </summary>
public sealed class AgentToolsSettingsTests : IDisposable
{
    private readonly string dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "coda-agenttools-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { }
    }

    private CodaSettings Load(string? userJson = null, string? projectJson = null)
    {
        var userDir = Path.Combine(this.dir, "user");
        var projectDir = Path.Combine(this.dir, "project");
        Directory.CreateDirectory(Path.Combine(userDir, ".coda"));
        Directory.CreateDirectory(Path.Combine(projectDir, ".coda"));

        if (userJson is not null)
        {
            File.WriteAllText(Path.Combine(userDir, ".coda", "settings.json"), userJson);
        }

        if (projectJson is not null)
        {
            File.WriteAllText(Path.Combine(projectDir, ".coda", "settings.json"), projectJson);
        }

        return SettingsLoader.Load(projectDir, userDir);
    }

    // ------------------------------------------------------------------
    // Absent block = no restriction
    // ------------------------------------------------------------------

    [Fact]
    public void Absent_agent_block_produces_null_filter()
    {
        var settings = this.Load(userJson: "{}", projectJson: "{}");

        Assert.Null(settings.AgentToolFilter);
    }

    [Fact]
    public void Absent_agent_tools_section_produces_null_filter()
    {
        var settings = this.Load(userJson: """{ "agent": {} }""");

        Assert.Null(settings.AgentToolFilter);
    }

    // ------------------------------------------------------------------
    // Basic parsing
    // ------------------------------------------------------------------

    [Fact]
    public void Allow_list_is_parsed()
    {
        var settings = this.Load(userJson: """
            { "agent": { "tools": { "allow": ["task", "task_start"] } } }
            """);

        var filter = settings.AgentToolFilter;
        Assert.NotNull(filter);
        Assert.NotNull(filter!.Allow);
        Assert.Contains("task", filter.Allow!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("task_start", filter.Allow!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deny_list_is_parsed()
    {
        var settings = this.Load(userJson: """
            { "agent": { "tools": { "deny": ["run_command"] } } }
            """);

        var filter = settings.AgentToolFilter;
        Assert.NotNull(filter);
        Assert.Contains("run_command", filter!.Deny, StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Empty allow array is honoured literally (NOT treated as absent)
    // ------------------------------------------------------------------

    [Fact]
    public void Empty_allow_array_is_honoured_literally_not_as_absent()
    {
        // An empty allow list blocks all tools and trips the inert-agent guard (because
        // neither task nor task_start passes). Verify the guard fires rather than the array
        // being silently treated as "no allowlist".
        Assert.Throws<InvalidOperationException>(() =>
            this.Load(userJson: """{ "agent": { "tools": { "allow": [] } } }"""));
    }

    // ------------------------------------------------------------------
    // Unknown tool names in either list are inert
    // ------------------------------------------------------------------

    [Fact]
    public void Unknown_allow_names_are_inert()
    {
        // "does_not_exist" is not a real tool, but "task" and "task_start" are present so
        // the inert-agent guard does not fire. The filter is created without error.
        var settings = this.Load(userJson: """
            { "agent": { "tools": { "allow": ["task", "task_start", "does_not_exist"] } } }
            """);

        Assert.NotNull(settings.AgentToolFilter);
    }

    [Fact]
    public void Unknown_deny_names_are_inert()
    {
        var settings = this.Load(userJson: """
            { "agent": { "tools": { "deny": ["does_not_exist"] } } }
            """);

        Assert.NotNull(settings.AgentToolFilter);
    }

    // ------------------------------------------------------------------
    // User/project merge
    // ------------------------------------------------------------------

    [Fact]
    public void Allow_is_intersected_across_files()
    {
        var settings = this.Load(
            userJson: """{ "agent": { "tools": { "allow": ["task", "task_start", "read_file"] } } }""",
            projectJson: """{ "agent": { "tools": { "allow": ["task", "task_start"] } } }""");

        var filter = settings.AgentToolFilter;
        Assert.NotNull(filter);
        Assert.NotNull(filter!.Allow);
        Assert.DoesNotContain("read_file", filter.Allow!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("task", filter.Allow!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("task_start", filter.Allow!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_file_cannot_widen_user_file_allowlist()
    {
        // User restricts to task/task_start; project tries to add write_file.
        var settings = this.Load(
            userJson: """{ "agent": { "tools": { "allow": ["task", "task_start"] } } }""",
            projectJson: """{ "agent": { "tools": { "allow": ["task", "task_start", "write_file"] } } }""");

        var filter = settings.AgentToolFilter;
        Assert.NotNull(filter!.Allow);
        Assert.DoesNotContain("write_file", filter!.Allow!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deny_is_unioned_across_files()
    {
        var settings = this.Load(
            userJson: """{ "agent": { "tools": { "deny": ["run_command"], "allow": ["task", "task_start", "run_command", "write_file"] } } }""",
            projectJson: """{ "agent": { "tools": { "deny": ["write_file"] } } }""");

        var filter = settings.AgentToolFilter;
        Assert.NotNull(filter);
        Assert.Contains("run_command", filter!.Deny, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("write_file", filter.Deny, StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Inert-agent guard
    // ------------------------------------------------------------------

    [Fact]
    public void Inert_agent_guard_fires_when_allow_excludes_both_task_and_task_start()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            this.Load(userJson: """{ "agent": { "tools": { "allow": ["read_file", "run_command"] } } }"""));

        Assert.Contains("task", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("task_start", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inert_agent_guard_fires_when_deny_covers_both_task_and_task_start()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            this.Load(userJson: """{ "agent": { "tools": { "deny": ["task", "task_start"] } } }"""));

        Assert.Contains("task", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inert_agent_guard_names_the_settings_file_in_the_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            this.Load(userJson: """{ "agent": { "tools": { "allow": ["read_file"] } } }"""));

        Assert.Contains(".coda", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guard_does_not_fire_when_task_passes_even_if_task_start_is_denied()
    {
        // Blocking only task_start is allowed as long as task itself passes.
        var settings = this.Load(userJson: """
            { "agent": { "tools": { "allow": ["task", "read_file"] } } }
            """);

        Assert.NotNull(settings.AgentToolFilter);
    }
}
