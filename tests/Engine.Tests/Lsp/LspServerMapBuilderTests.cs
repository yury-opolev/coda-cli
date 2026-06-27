using System.Text.Json.Nodes;
using Coda.Agent.Lsp;

namespace Engine.Tests.Lsp;

public sealed class LspServerMapBuilderTests : IDisposable
{
    private readonly string baseDir;

    public LspServerMapBuilderTests()
    {
        this.baseDir = Path.Combine(Path.GetTempPath(), "LspMapBuilderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.baseDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.baseDir))
        {
            Directory.Delete(this.baseDir, recursive: true);
        }
    }

    // ---------- helpers ----------

    private string CreatePlugin(string pluginName, string serverName, string command)
    {
        var pluginDir = Path.Combine(this.baseDir, pluginName);
        Directory.CreateDirectory(pluginDir);
        var pluginJson = $$"""
            {
                "name": "{{pluginName}}",
                "lspServers": {
                    "{{serverName}}": {
                        "command": "{{command}}",
                        "extensionToLanguage": { ".py": "python" }
                    }
                }
            }
            """;
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), pluginJson);
        return pluginDir;
    }

    private static LspServerConfig MakeConfig(string command) =>
        new LspServerConfig(
            command,
            [],
            new Dictionary<string, string> { [".ts"] = "typescript" },
            null,
            null,
            null);

    // ---------- tests ----------

    [Fact]
    public void Merges_plugin_and_settings_servers()
    {
        CreatePlugin("myplugin", "py", "pylsp");

        var settingsServers = new Dictionary<string, LspServerConfig>
        {
            ["ts"] = MakeConfig("typescript-language-server"),
        };

        var result = LspServerMapBuilder.Build(settingsServers, [this.baseDir]);

        Assert.True(result.ContainsKey("ts"), "Expected 'ts' from settings.");
        Assert.True(result.ContainsKey("plugin:myplugin:py"), "Expected 'plugin:myplugin:py' from plugin.");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Settings_wins_on_exact_key_clash()
    {
        // Construct a scenario where the settings map has a key that also appears
        // in the plugin servers. We simulate the overlay directly by having a
        // settings key present; the settings value must win.
        CreatePlugin("myplugin", "py", "pylsp-from-plugin");

        // The plugin produces "plugin:myplugin:py"; we add that exact key to settings
        // to force a clash and verify the settings value wins.
        var settingsConfig = MakeConfig("pylsp-from-settings");
        var settingsServers = new Dictionary<string, LspServerConfig>
        {
            ["plugin:myplugin:py"] = settingsConfig,
        };

        var result = LspServerMapBuilder.Build(settingsServers, [this.baseDir]);

        // The settings config must win on the clashing key.
        Assert.Equal("pylsp-from-settings", result["plugin:myplugin:py"].Command);
    }

    [Fact]
    public void No_plugins_returns_only_settings()
    {
        var settingsServers = new Dictionary<string, LspServerConfig>
        {
            ["ts"] = MakeConfig("typescript-language-server"),
        };

        // Pass a non-existent dir — no plugins
        var nonExistent = Path.Combine(this.baseDir, "no-such-dir");
        var result = LspServerMapBuilder.Build(settingsServers, [nonExistent]);

        Assert.Single(result);
        Assert.True(result.ContainsKey("ts"));
    }

    [Fact]
    public void No_settings_returns_only_plugins()
    {
        CreatePlugin("myplugin", "py", "pylsp");

        var result = LspServerMapBuilder.Build(
            new Dictionary<string, LspServerConfig>(),
            [this.baseDir]);

        Assert.Single(result);
        Assert.True(result.ContainsKey("plugin:myplugin:py"));
    }
}
