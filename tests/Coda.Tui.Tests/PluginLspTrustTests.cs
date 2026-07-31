using Coda.Agent.Lsp;
using Coda.Tui.Plugins;

namespace Coda.Tui.Tests;

/// <summary>
/// A plugin's LSP servers run a process, exactly as its hooks and MCP servers do, so they must pass
/// the same gates. They used to pass none: the loader enumerated the plugin directories itself,
/// bypassing the composer, so neither the per-class approval nor the enabled flag was consulted and a
/// disabled plugin could still start a command.
/// </summary>
public sealed class PluginLspTrustTests : IDisposable
{
    private readonly string tempDir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "coda-lsp-trust-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch (IOException) { }
    }

    private string CreatePlugin(string name, string? version = "1.0.0")
    {
        var dir = Path.Combine(this.tempDir, "plugins", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "plugin.json"),
            $$"""
            {
              "name": "{{name}}",
              "version": "{{version}}",
              "lspServers": { "srv": { "command": "node", "args": ["lsp.js"], "extensionToLanguage": { ".x": "xlang" } } }
            }
            """);
        return dir;
    }

    private PluginInfo Info(string name)
    {
        var dir = Path.Combine(this.tempDir, "plugins", name);
        var manifest = PluginManifestParser.Parse(File.ReadAllText(Path.Combine(dir, "plugin.json")), dir);
        return new PluginInfo(name, "1.0.0", string.Empty, dir) { Manifest = manifest };
    }

    // -----------------------------------------------------------------------
    // The component class exists at all
    // -----------------------------------------------------------------------

    [Fact]
    public void Lsp_is_a_component_class_a_user_can_approve_or_refuse()
    {
        Assert.Contains(PluginComponentClass.Lsp, Enum.GetValues<PluginComponentClass>());
    }

    [Fact]
    public void A_plugin_declaring_an_lsp_server_reports_the_lsp_class()
    {
        this.CreatePlugin("alpha");

        var inventory = PluginInventory.FromManifest(this.Info("alpha").Manifest, Path.Combine(this.tempDir, "plugins", "alpha"));

        Assert.Contains(PluginComponentClass.Lsp, inventory.PresentClasses);
        Assert.False(inventory.IsEmpty);
    }

    [Fact]
    public void The_inventory_summary_mentions_the_lsp_server()
    {
        this.CreatePlugin("alpha");

        var summary = PluginInventory.FromManifest(this.Info("alpha").Manifest, Path.Combine(this.tempDir, "plugins", "alpha")).ToDisplayString();

        Assert.Contains("LSP", summary, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Loading only the plugins that were allowed
    // -----------------------------------------------------------------------

    [Fact]
    public void Only_the_plugin_directories_it_is_given_are_loaded()
    {
        var allowed = this.CreatePlugin("allowed");
        this.CreatePlugin("refused");

        var servers = PluginLspServerLoader.LoadForPluginDirectories([allowed]);

        Assert.Single(servers);
        Assert.Contains(servers, kvp => kvp.Key.Contains("allowed", StringComparison.Ordinal));
        Assert.DoesNotContain(servers, kvp => kvp.Key.Contains("refused", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_allow_list_loads_nothing()
    {
        this.CreatePlugin("alpha");

        Assert.Empty(PluginLspServerLoader.LoadForPluginDirectories([]));
    }

    [Fact]
    public void A_directory_that_is_not_a_plugin_is_skipped_rather_than_throwing()
    {
        var notAPlugin = Path.Combine(this.tempDir, "empty");
        Directory.CreateDirectory(notAPlugin);

        Assert.Empty(PluginLspServerLoader.LoadForPluginDirectories([notAPlugin]));
    }

    // -----------------------------------------------------------------------
    // Content hash covers the declaration, so an update re-prompts
    // -----------------------------------------------------------------------

    [Fact]
    public void Changing_an_lsp_command_changes_the_content_hash()
    {
        var dir = this.CreatePlugin("alpha");
        var before = PluginContentHash.Compute(this.Info("alpha"));

        File.WriteAllText(
            Path.Combine(dir, "plugin.json"),
            """
            {
              "name": "alpha",
              "version": "1.0.0",
              "lspServers": { "srv": { "command": "evil", "args": ["x.js"], "extensionToLanguage": { ".x": "xlang" } } }
            }
            """);

        Assert.NotEqual(before, PluginContentHash.Compute(this.Info("alpha")));
    }
}

/// <summary>
/// End-to-end wiring. Gating the loader is only half the job: if the composed set never reaches the
/// session, plugin LSP servers go from ungated to silently dead — a quieter failure than the one being
/// fixed, and one nothing else would notice.
/// </summary>
public sealed class PluginLspCompositionTests : IDisposable
{
    private readonly string tempDir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "coda-lsp-comp-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch (IOException) { }
    }

    private PluginInfo CreatePlugin(string name, bool enabled = true)
    {
        var dir = Path.Combine(this.tempDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "plugin.json"),
            $$"""
            {
              "name": "{{name}}",
              "version": "1.0.0",
              "lspServers": { "srv": { "command": "node", "args": ["lsp.js"], "extensionToLanguage": { ".x": "xlang" } } }
            }
            """);

        var manifest = PluginManifestParser.Parse(File.ReadAllText(Path.Combine(dir, "plugin.json")), dir);
        return new PluginInfo(name, "1.0.0", string.Empty, dir) { Manifest = manifest, IsEnabled = enabled };
    }

    [Fact]
    public void An_enabled_plugin_contributes_its_lsp_server()
    {
        var composition = PluginComponentComposer.Compose(
            [this.CreatePlugin("alpha")], this.tempDir);

        Assert.Contains(composition.LspServers, kvp => kvp.Key.Contains("alpha", StringComparison.Ordinal));
    }

    [Fact]
    public void A_disabled_plugin_contributes_nothing()
    {
        // The old loader enumerated directories, so it started this server regardless.
        var composition = PluginComponentComposer.Compose(
            [this.CreatePlugin("alpha", enabled: false)], this.tempDir);

        Assert.Empty(composition.LspServers);
    }

    [Fact]
    public void The_settings_map_still_wins_on_a_key_clash()
    {
        var composition = PluginComponentComposer.Compose(
            [this.CreatePlugin("alpha")], this.tempDir);
        var key = composition.LspServers.Keys.Single();

        var merged = Coda.Agent.Lsp.LspServerMapBuilder.Build(
            new Dictionary<string, Coda.Agent.Lsp.LspServerConfig>
            {
                [key] = new(
                    "from-settings",
                    [],
                    new Dictionary<string, string> { [".x"] = "xlang" },
                    new Dictionary<string, string>(),
                    null,
                    null),
            },
            composition.LspServers);

        Assert.Equal("from-settings", merged[key].Command);
    }
}
