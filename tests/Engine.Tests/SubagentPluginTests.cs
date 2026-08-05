using Coda.Agent.Subagents;
using Coda.Tui.Plugins;
using Engine.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

/// <summary>
/// Tests for Phase 4 plugin-supplied subagent definitions (Tests 1 and 2 of the spec).
/// </summary>
public sealed class SubagentPluginTests : IDisposable
{
    private readonly TempDir temp = new();

    public void Dispose() => this.temp.Dispose();

    // =========================================================================
    // SubagentRegistry
    // =========================================================================

    [Fact]
    public void SubagentRegistry_resolves_plugin_agent_by_type()
    {
        var definition = new SubagentDefinition(
            Type: "my-reviewer",
            Description: "A code review specialist",
            SystemPromptBody: "You review code.",
            ReadOnlyToolsOnly: true);

        var registry = new SubagentRegistry([definition]);
        var resolved = registry.Resolve("my-reviewer");

        Assert.Equal("my-reviewer", resolved.Type);
        Assert.Equal("You review code.", resolved.SystemPromptBody);
        Assert.True(resolved.ReadOnlyToolsOnly);
    }

    [Fact]
    public void SubagentRegistry_resolves_plugin_agent_case_insensitively()
    {
        var definition = new SubagentDefinition("my-reviewer", "Desc", "Body", false);
        var registry = new SubagentRegistry([definition]);

        Assert.Equal("my-reviewer", registry.Resolve("MY-REVIEWER").Type);
        Assert.Equal("my-reviewer", registry.Resolve("My-Reviewer").Type);
    }

    [Fact]
    public void SubagentRegistry_falls_back_to_built_in_for_unknown_type()
    {
        var registry = new SubagentRegistry([]);
        var resolved = registry.Resolve("general-purpose");

        // Falls back to BuiltInAgents — should return the general-purpose built-in.
        Assert.Equal("general-purpose", resolved.Type);
    }

    [Fact]
    public void SubagentRegistry_null_registry_falls_back_to_built_in()
    {
        // When no plugin agents are provided, unknown types still resolve to general-purpose.
        var registry = new SubagentRegistry(null);
        var resolved = registry.Resolve("totally-unknown-type");
        Assert.Equal("general-purpose", resolved.Type);
    }

    [Fact]
    public void SubagentRegistry_plugin_agent_shadows_unknown_built_in()
    {
        // A plugin-registered type name that is not a built-in resolves to the plugin definition.
        var definition = new SubagentDefinition(
            "custom-type", "Custom", "Custom body", false);
        var registry = new SubagentRegistry([definition]);

        var resolved = registry.Resolve("custom-type");
        Assert.Equal("custom-type", resolved.Type);
        Assert.Equal("Custom body", resolved.SystemPromptBody);
    }

    // =========================================================================
    // PluginAgentLoader — directory loading
    // =========================================================================

    [Fact]
    public void PluginAgentLoader_loads_agent_from_md_file()
    {
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "reviewer.md"),
            "---\ntype: my-reviewer\ndescription: Specialist\n---\nYou review code thoroughly.");

        var agents = PluginAgentLoader.LoadFromDirectory(agentsDir, "test-plugin");

        Assert.Single(agents);
        Assert.Equal("my-reviewer", agents[0].Type);
        Assert.Equal("Specialist", agents[0].Description);
        Assert.Equal("You review code thoroughly.", agents[0].SystemPromptBody);
    }

    [Fact]
    public void PluginAgentLoader_reads_read_only_tools_flag()
    {
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "readonly.md"),
            "---\ntype: readonly-agent\nread-only-tools: true\n---\nRead only.");

        var agents = PluginAgentLoader.LoadFromDirectory(agentsDir, "test-plugin");

        Assert.Single(agents);
        Assert.True(agents[0].ReadOnlyToolsOnly);
    }

    [Fact]
    public void PluginAgentLoader_logs_error_for_hooks_key_but_still_loads_agent()
    {
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "bad-hooks.md"),
            "---\ntype: bad-agent\nhooks: ./hook.sh\n---\nBody text.");

        var logger = new CapturingLogger();
        var agents = PluginAgentLoader.LoadFromDirectory(agentsDir, "test-plugin", logger);

        // Agent still loads despite the forbidden key.
        Assert.Single(agents);
        Assert.Equal("bad-agent", agents[0].Type);

        // Error must be logged mentioning the forbidden key.
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("hooks", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PluginAgentLoader_logs_error_for_mcpServers_key_but_still_loads_agent()
    {
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "bad-mcp.md"),
            "---\ntype: mcp-agent\nmcpServers: ./servers.json\n---\nBody.");

        var logger = new CapturingLogger();
        var agents = PluginAgentLoader.LoadFromDirectory(agentsDir, "test-plugin", logger);

        Assert.Single(agents);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("mcp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PluginAgentLoader_logs_error_for_permissions_key_but_still_loads_agent()
    {
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "bad-perms.md"),
            "---\ntype: perms-agent\npermissions: allow-all\n---\nBody.");

        var logger = new CapturingLogger();
        var agents = PluginAgentLoader.LoadFromDirectory(agentsDir, "test-plugin", logger);

        Assert.Single(agents);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("permission", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PluginAgentLoader_logs_error_for_permissionMode_key_but_still_loads_agent()
    {
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "bad-mode.md"),
            "---\ntype: mode-agent\npermissionMode: allowAll\n---\nBody.");

        var logger = new CapturingLogger();
        var agents = PluginAgentLoader.LoadFromDirectory(agentsDir, "test-plugin", logger);

        Assert.Single(agents);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("permission", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PluginAgentLoader_skips_file_without_frontmatter()
    {
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "no-frontmatter.md"), "Just plain text, no frontmatter.");

        var agents = PluginAgentLoader.LoadFromDirectory(agentsDir, "test-plugin");

        Assert.Empty(agents);
    }

    [Fact]
    public void PluginAgentLoader_skips_file_without_type_field()
    {
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "no-type.md"),
            "---\ndescription: No type declared\n---\nBody.");

        var logger = new CapturingLogger();
        var agents = PluginAgentLoader.LoadFromDirectory(agentsDir, "test-plugin", logger);

        Assert.Empty(agents);
    }

    [Fact]
    public void PluginAgentLoader_returns_empty_when_directory_missing()
    {
        var nonExistent = Path.Combine(this.temp.Path, "does-not-exist");
        var agents = PluginAgentLoader.LoadFromDirectory(nonExistent, "test-plugin");
        Assert.Empty(agents);
    }

    [Fact]
    public void PluginAgentLoader_Load_returns_empty_for_disabled_plugin()
    {
        var plugin = new PluginInfo("my-plugin", "1.0.0", "Test", this.temp.Path)
        {
            IsEnabled = false,
        };

        var agents = PluginAgentLoader.Load(plugin, workingDirectory: null); // user-scoped: no working directory needed
        Assert.Empty(agents);
    }

    [Fact]
    public void PluginAgentLoader_Load_uses_manifest_agents_path_override()
    {
        var customDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "my-agents")).FullName;
        File.WriteAllText(Path.Combine(customDir, "custom.md"),
            "---\ntype: custom-agent\n---\nCustom body.");

        var manifest = new PluginManifest { Name = "test-plugin", Version = "1.0.0", Agents = "my-agents" };
        var plugin = new PluginInfo("test-plugin", "1.0.0", "Test", this.temp.Path)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        var agents = PluginAgentLoader.Load(plugin, workingDirectory: null); // user-scoped: no working directory needed
        Assert.Single(agents);
        Assert.Equal("custom-agent", agents[0].Type);
    }

    [Fact]
    public void PluginComponentComposer_Compose_includes_plugin_agents()
    {
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "composer-agent.md"),
            "---\ntype: composer-type\ndescription: Composed\n---\nComposed body.");

        var manifest = new PluginManifest { Name = "test-plugin", Version = "1.0.0" };
        var plugin = new PluginInfo("test-plugin", "1.0.0", "Test", this.temp.Path)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        var composition = PluginComponentComposer.Compose([plugin], this.temp.Path);

        Assert.Contains(composition.Agents, a => a.Type == "composer-type");
    }

    // =========================================================================
    // Plugin model: user-scoped vs project-scoped (Task 3)
    // =========================================================================

    [Fact]
    public void PluginAgentLoader_reads_model_key_from_user_scoped_plugin()
    {
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "model-agent.md"),
            "---\ntype: model-agent\nmodel: fast-cheap-model\n---\nBody.");

        var agents = PluginAgentLoader.LoadFromDirectory(
            agentsDir, "test-plugin", isProjectPlugin: false);

        Assert.Single(agents);
        Assert.Equal("fast-cheap-model", agents[0].Model);
    }

    [Fact]
    public void PluginAgentLoader_ignores_model_key_from_project_scoped_plugin_with_warning()
    {
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "proj-model-agent.md"),
            "---\ntype: proj-model-agent\nmodel: expensive-model\n---\nBody.");

        var logger = new CapturingLogger();
        var agents = PluginAgentLoader.LoadFromDirectory(
            agentsDir, "test-plugin", isProjectPlugin: true, logger: logger);

        // Agent still loads, but model must be null.
        Assert.Single(agents);
        Assert.Null(agents[0].Model);

        // A warning must have been logged.
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("model", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PluginAgentLoader_no_model_key_leaves_definition_model_null()
    {
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "no-model.md"),
            "---\ntype: no-model-agent\n---\nBody.");

        var agents = PluginAgentLoader.LoadFromDirectory(agentsDir, "test-plugin");

        Assert.Single(agents);
        Assert.Null(agents[0].Model);
    }
}
