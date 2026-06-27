using System.Text.Json;
using Coda.Agent.Lsp;

namespace Engine.Tests.Lsp;

public sealed class PluginLspServerLoaderTests : IDisposable
{
    private readonly string baseDir;

    public PluginLspServerLoaderTests()
    {
        this.baseDir = Path.Combine(Path.GetTempPath(), "PluginLspTests_" + Guid.NewGuid().ToString("N"));
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

    private string CreatePlugin(string pluginName, string pluginJsonContent)
    {
        var pluginDir = Path.Combine(this.baseDir, pluginName);
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), pluginJsonContent);
        return pluginDir;
    }

    private static string MinimalServerJson(string command, string ext = ".ts", string lang = "typescript")
        => $$$"""{"command":"{{{command}}}","extensionToLanguage":{"{{{ext}}}":"{{{lang}}}"}}""";

    // ---------- tests ----------

    [Fact]
    public void Inline_lspServers_in_plugin_json_loaded_and_scoped()
    {
        CreatePlugin("myplugin", """
            {
              "name": "myplugin",
              "lspServers": {
                "ts": {
                  "command": "tsls",
                  "extensionToLanguage": { ".ts": "typescript" }
                }
              }
            }
            """);

        var result = PluginLspServerLoader.Load([this.baseDir]);

        Assert.True(result.ContainsKey("plugin:myplugin:ts"), "Expected key plugin:myplugin:ts");
        Assert.Equal("tsls", result["plugin:myplugin:ts"].Command);
    }

    [Fact]
    public void Lsp_json_file_loaded()
    {
        var pluginDir = CreatePlugin("pyplugin", """{"name":"pyplugin"}""");
        File.WriteAllText(
            Path.Combine(pluginDir, ".lsp.json"),
            """{"py":{"command":"pyls","extensionToLanguage":{".py":"python"}}}""");

        var result = PluginLspServerLoader.Load([this.baseDir]);

        Assert.True(result.ContainsKey("plugin:pyplugin:py"), "Expected key plugin:pyplugin:py");
        Assert.Equal("pyls", result["plugin:pyplugin:py"].Command);
    }

    [Fact]
    public void String_path_declaration_loaded()
    {
        var pluginDir = CreatePlugin("strplugin", """
            {
              "name": "strplugin",
              "lspServers": "servers.json"
            }
            """);
        File.WriteAllText(
            Path.Combine(pluginDir, "servers.json"),
            """{"rb":{"command":"rubocop","extensionToLanguage":{".rb":"ruby"}}}""");

        var result = PluginLspServerLoader.Load([this.baseDir]);

        Assert.True(result.ContainsKey("plugin:strplugin:rb"), "Expected key plugin:strplugin:rb");
    }

    [Fact]
    public void Path_traversal_declaration_rejected()
    {
        // Create the escape file one level above the base dir
        var escapePath = Path.Combine(Path.GetTempPath(), "escape_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(escapePath, """{"evil":{"command":"evil","extensionToLanguage":{".x":"x"}}}""");

        try
        {
            // plugin.json references ../escape.json (traversal)
            CreatePlugin("travplugin", """
                {
                  "name": "travplugin",
                  "lspServers": "../escape.json"
                }
                """);

            var result = PluginLspServerLoader.Load([this.baseDir]);

            // Should not throw and should not contain the traversal server
            Assert.DoesNotContain(result.Keys, k => k.Contains("evil"));
        }
        finally
        {
            if (File.Exists(escapePath))
            {
                File.Delete(escapePath);
            }
        }
    }

    [Fact]
    public void Claude_plugin_root_resolved_in_command_and_injected_into_env()
    {
        var pluginDir = CreatePlugin("rootplugin", """
            {
              "name": "rootplugin",
              "lspServers": {
                "x": {
                  "command": "${CLAUDE_PLUGIN_ROOT}/bin/ls",
                  "args": ["--flag", "${CLAUDE_PLUGIN_ROOT}"],
                  "extensionToLanguage": { ".x": "x" }
                }
              }
            }
            """);

        var result = PluginLspServerLoader.Load([this.baseDir]);

        Assert.True(result.ContainsKey("plugin:rootplugin:x"), "Expected key plugin:rootplugin:x");
        var cfg = result["plugin:rootplugin:x"];

        Assert.StartsWith(pluginDir, cfg.Command, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(pluginDir, cfg.Args[1], StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(cfg.Env);
        Assert.True(cfg.Env!.ContainsKey("CLAUDE_PLUGIN_ROOT"), "Expected CLAUDE_PLUGIN_ROOT in Env");
        Assert.Equal(pluginDir, cfg.Env["CLAUDE_PLUGIN_ROOT"], StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Malformed_plugin_json_skipped_others_kept()
    {
        // bad plugin: garbage plugin.json
        var badDir = Path.Combine(this.baseDir, "badplugin");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "plugin.json"), "NOT JSON {{{{");

        // good plugin
        CreatePlugin("goodplugin", """
            {
              "name": "goodplugin",
              "lspServers": {
                "go": { "command": "gopls", "extensionToLanguage": { ".go": "go" } }
              }
            }
            """);

        var result = PluginLspServerLoader.Load([this.baseDir]);

        Assert.True(result.ContainsKey("plugin:goodplugin:go"), "Valid plugin should be loaded");
        Assert.DoesNotContain(result.Keys, k => k.StartsWith("plugin:badplugin:", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_plugins_both_namespaced()
    {
        CreatePlugin("alpha", """{"name":"alpha","lspServers":{"a":{"command":"acmd","extensionToLanguage":{".a":"alang"}}}}""");
        CreatePlugin("beta", """{"name":"beta","lspServers":{"b":{"command":"bcmd","extensionToLanguage":{".b":"blang"}}}}""");

        var result = PluginLspServerLoader.Load([this.baseDir]);

        Assert.True(result.ContainsKey("plugin:alpha:a"), "Expected plugin:alpha:a");
        Assert.True(result.ContainsKey("plugin:beta:b"), "Expected plugin:beta:b");
    }

    [Fact]
    public void No_plugin_dirs_returns_empty()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N"));

        var result = PluginLspServerLoader.Load([nonExistent]);

        Assert.Empty(result);
    }

    [Fact]
    public void Array_declaration_mixed()
    {
        var pluginDir = CreatePlugin("arrayplugin", """
            {
              "name": "arrayplugin",
              "lspServers": [
                { "a": { "command": "acmd", "extensionToLanguage": { ".a": "alang" } } },
                "more.json"
              ]
            }
            """);
        File.WriteAllText(
            Path.Combine(pluginDir, "more.json"),
            """{"b":{"command":"bcmd","extensionToLanguage":{".b":"blang"}}}""");

        var result = PluginLspServerLoader.Load([this.baseDir]);

        Assert.True(result.ContainsKey("plugin:arrayplugin:a"), "Expected plugin:arrayplugin:a");
        Assert.True(result.ContainsKey("plugin:arrayplugin:b"), "Expected plugin:arrayplugin:b from more.json");
    }
}
