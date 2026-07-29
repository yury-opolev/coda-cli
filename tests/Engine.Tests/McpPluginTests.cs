using Coda.Mcp;
using Coda.Tui.Plugins;
using Engine.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

/// <summary>
/// Tests for Phase 4 plugin-supplied MCP servers (Test 3 of the spec).
/// </summary>
public sealed class McpPluginTests : IDisposable
{
    private readonly TempDir temp = new();

    public void Dispose() => this.temp.Dispose();

    // =========================================================================
    // McpConfig.LoadWithPlugins
    // =========================================================================

    [Fact]
    public void LoadWithPlugins_includes_plugin_server_when_no_user_or_project_entry()
    {
        var pluginServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["plugin-server"] = new McpStdioServerConfig("node", ["plugin.js"], new Dictionary<string, string>()),
        };

        var result = McpConfig.LoadWithPlugins(
            workingDirectory: this.temp.Path,
            pluginServers: pluginServers);

        Assert.True(result.ContainsKey("plugin-server"));
    }

    [Fact]
    public void LoadWithPlugins_user_server_wins_over_plugin_server_same_name()
    {
        var userMcpFile = Path.Combine(this.temp.Path, "user");
        Directory.CreateDirectory(userMcpFile);
        File.WriteAllText(Path.Combine(userMcpFile, ".mcp.json"),
            """{"mcpServers":{"shared-server":{"command":"user-server","args":[]}}}""");

        var pluginServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["shared-server"] = new McpStdioServerConfig("plugin-server", ["plugin.js"], new Dictionary<string, string>()),
        };

        var result = McpConfig.LoadWithPlugins(
            workingDirectory: this.temp.Path,
            pluginServers: pluginServers,
            userMcpDir: userMcpFile);

        Assert.True(result.ContainsKey("shared-server"));
        var config = Assert.IsType<McpStdioServerConfig>(result["shared-server"]);
        // User entry wins: command should be "user-server".
        Assert.Equal("user-server", config.Command);
    }

    [Fact]
    public void LoadWithPlugins_project_server_wins_over_plugin_server_same_name()
    {
        // Write a project-level .mcp.json.
        File.WriteAllText(
            Path.Combine(this.temp.Path, ".mcp.json"),
            """{"mcpServers":{"shared-server":{"command":"project-server","args":[]}}}""");

        var pluginServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["shared-server"] = new McpStdioServerConfig("plugin-server", ["plugin.js"], new Dictionary<string, string>()),
        };

        var result = McpConfig.LoadWithPlugins(
            workingDirectory: this.temp.Path,
            pluginServers: pluginServers);

        var config = Assert.IsType<McpStdioServerConfig>(result["shared-server"]);
        Assert.Equal("project-server", config.Command);
    }

    [Fact]
    public void LoadWithPlugins_null_plugin_servers_behaves_like_no_plugins()
    {
        // Use a dedicated isolated user MCP dir with no .mcp.json.
        var isolatedUserDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "isolated-user")).FullName;

        var result = McpConfig.LoadWithPlugins(
            workingDirectory: this.temp.Path,
            pluginServers: null,
            userMcpDir: isolatedUserDir);

        // No files exist so no servers should be present.
        Assert.Empty(result);
    }

    // =========================================================================
    // McpConfig.LoadEntriesWithPlugins
    // =========================================================================

    [Fact]
    public void LoadEntriesWithPlugins_plugin_entry_has_Plugin_scope_and_plugin_name()
    {
        // Isolated user dir to avoid picking up real user MCP config.
        var isolatedUserDir = Directory.CreateDirectory(Path.Combine(this.temp.Path, "isolated-user")).FullName;

        var pluginServers = new Dictionary<string, (McpServerConfig Config, string PluginName)>(StringComparer.Ordinal)
        {
            ["my-tool"] = (new McpStdioServerConfig("tool", [], new Dictionary<string, string>()), "my-plugin"),
        };

        var entries = McpConfig.LoadEntriesWithPlugins(
            workingDirectory: this.temp.Path,
            pluginServersByName: pluginServers,
            userMcpDir: isolatedUserDir);

        var entry = Assert.Single(entries);
        Assert.Equal("my-tool", entry.Name);
        Assert.Equal(McpConfigScope.Plugin, entry.Scope);
        Assert.Equal("my-plugin", entry.PluginName);
    }

    [Fact]
    public void LoadEntriesWithPlugins_plugin_entry_shadowed_by_user_entry_is_omitted()
    {
        var userMcpDir = Path.Combine(this.temp.Path, "userconfig");
        Directory.CreateDirectory(userMcpDir);
        File.WriteAllText(
            Path.Combine(userMcpDir, ".mcp.json"),
            """{"mcpServers":{"my-tool":{"command":"user-tool","args":[]}}}""");

        var pluginServers = new Dictionary<string, (McpServerConfig, string)>(StringComparer.Ordinal)
        {
            ["my-tool"] = (new McpStdioServerConfig("plugin-tool", [], new Dictionary<string, string>()), "my-plugin"),
        };

        var entries = McpConfig.LoadEntriesWithPlugins(
            workingDirectory: this.temp.Path,
            pluginServersByName: pluginServers,
            userMcpDir: userMcpDir);

        // The plugin entry for "my-tool" should be omitted because a user entry exists.
        var entry = Assert.Single(entries);
        Assert.Equal(McpConfigScope.User, entry.Scope);
        var config = Assert.IsType<McpStdioServerConfig>(entry.Config);
        Assert.Equal("user-tool", config.Command);
    }

    // =========================================================================
    // PluginMcpLoader
    // =========================================================================

    [Fact]
    public void PluginMcpLoader_loads_servers_from_plugin_mcp_files()
    {
        var mcpFile = Path.Combine(this.temp.Path, "servers.json");
        File.WriteAllText(mcpFile,
            """{"mcpServers":{"my-tool":{"command":"node","args":["server.js"]}}}""");

        var manifest = new PluginManifest
        {
            Name = "test-plugin",
            Version = "1.0.0",
            McpServers = ["servers.json"],
        };
        var plugin = new PluginInfo("test-plugin", "1.0.0", "Test", this.temp.Path)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        var result = PluginMcpLoader.Load([plugin]);

        Assert.True(result.ContainsKey("my-tool"));
        var (config, pluginName) = result["my-tool"];
        Assert.Equal("test-plugin", pluginName);
        var stdio = Assert.IsType<McpStdioServerConfig>(config);
        Assert.Equal("node", stdio.Command);
    }

    [Fact]
    public void PluginMcpLoader_disabled_plugin_contributes_no_servers()
    {
        var mcpFile = Path.Combine(this.temp.Path, "servers.json");
        File.WriteAllText(mcpFile,
            """{"mcpServers":{"my-tool":{"command":"node","args":[]}}}""");

        var manifest = new PluginManifest
        {
            Name = "disabled-plugin",
            Version = "1.0.0",
            McpServers = ["servers.json"],
        };
        var plugin = new PluginInfo("disabled-plugin", "1.0.0", "Test", this.temp.Path)
        {
            IsEnabled = false,
            Manifest = manifest,
        };

        var result = PluginMcpLoader.Load([plugin]);

        Assert.Empty(result);
    }

    [Fact]
    public void PluginMcpLoader_logs_warning_for_missing_mcp_file()
    {
        var manifest = new PluginManifest
        {
            Name = "test-plugin",
            Version = "1.0.0",
            McpServers = ["nonexistent.json"],
        };
        var plugin = new PluginInfo("test-plugin", "1.0.0", "Test", this.temp.Path)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        var logger = new CapturingLogger();
        var result = PluginMcpLoader.Load([plugin], logger);

        Assert.Empty(result);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PluginMcpLoader_logs_error_for_malformed_mcp_file_but_continues()
    {
        var goodFile = Path.Combine(this.temp.Path, "good.json");
        var badFile = Path.Combine(this.temp.Path, "bad.json");
        File.WriteAllText(goodFile,
            """{"mcpServers":{"good-tool":{"command":"ok","args":[]}}}""");
        File.WriteAllText(badFile, "NOT VALID JSON {{{");

        // Create two plugins — one with a good file, one with a bad file.
        var dir2 = Directory.CreateDirectory(Path.Combine(this.temp.Path, "plugin2")).FullName;
        File.WriteAllText(Path.Combine(dir2, "bad.json"), "NOT VALID JSON {{{");

        var manifest1 = new PluginManifest { Name = "plugin1", Version = "1.0.0", McpServers = ["good.json"] };
        var manifest2 = new PluginManifest { Name = "plugin2", Version = "1.0.0", McpServers = ["bad.json"] };
        var plugin1 = new PluginInfo("plugin1", "1.0.0", "Plugin 1", this.temp.Path) { IsEnabled = true, Manifest = manifest1 };
        var plugin2 = new PluginInfo("plugin2", "1.0.0", "Plugin 2", dir2) { IsEnabled = true, Manifest = manifest2 };

        var logger = new CapturingLogger();
        var result = PluginMcpLoader.Load([plugin1, plugin2], logger);

        // Good server from plugin1 should still load.
        Assert.True(result.ContainsKey("good-tool"));
        // Malformed file from plugin2 should produce an error log.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    // =========================================================================
    // PluginComponentComposer MCP integration
    // =========================================================================

    [Fact]
    public void PluginComponentComposer_Compose_includes_plugin_mcp_servers()
    {
        var mcpFile = Path.Combine(this.temp.Path, "servers.json");
        File.WriteAllText(mcpFile,
            """{"mcpServers":{"compose-tool":{"command":"node","args":[]}}}""");

        var manifest = new PluginManifest
        {
            Name = "test-plugin",
            Version = "1.0.0",
            McpServers = ["servers.json"],
        };
        var plugin = new PluginInfo("test-plugin", "1.0.0", "Test", this.temp.Path)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        var composition = PluginComponentComposer.Compose([plugin], this.temp.Path);

        Assert.True(composition.McpServers.ContainsKey("compose-tool"));
        Assert.Equal("test-plugin", composition.McpServers["compose-tool"].PluginName);
    }
}
