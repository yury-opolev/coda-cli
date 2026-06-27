namespace Coda.Tui.Tests;

using System.IO;
using Coda.Tui.Plugins;

public sealed class MarketplaceManagerTests : IDisposable
{
    private readonly string tempDir;
    private readonly string fixtureDir;

    public MarketplaceManagerTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-mm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);

        // Build the fixture marketplace directory:
        //   fx/.claude-plugin/marketplace.json  (name: "fixture", pluginRoot: "plugins", 1 plugin "demo" source:"demo")
        //   fx/plugins/demo/plugin.json
        this.fixtureDir = Path.Combine(this.tempDir, "fx");
        var claudePluginDir = Path.Combine(this.fixtureDir, ".claude-plugin");
        Directory.CreateDirectory(claudePluginDir);

        File.WriteAllText(
            Path.Combine(claudePluginDir, "marketplace.json"),
            """
            {
              "name": "fixture",
              "metadata": { "pluginRoot": "plugins" },
              "plugins": [
                { "name": "demo", "source": "demo", "description": "Demo" }
              ]
            }
            """);

        var demoPluginDir = Path.Combine(this.fixtureDir, "plugins", "demo");
        Directory.CreateDirectory(demoPluginDir);
        File.WriteAllText(
            Path.Combine(demoPluginDir, "plugin.json"),
            """{"name":"demo","version":"1.0.0","description":"Demo"}""");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.tempDir))
            {
                Directory.Delete(this.tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private MarketplaceManager CreateManager() => new(this.tempDir);

    // ── Add from local directory ─────────────────────────────────────────────

    [Fact]
    public async Task Add_from_local_directory_registers_and_caches()
    {
        var manager = this.CreateManager();

        var (ok, message) = await manager.AddAsync(this.fixtureDir, CancellationToken.None);

        Assert.True(ok, message);
        var list = manager.List();
        Assert.Contains(list, x => x.Name == "fixture");
        var cachedManifest = Path.Combine(this.tempDir, "marketplaces", "fixture", ".claude-plugin", "marketplace.json");
        Assert.True(File.Exists(cachedManifest), $"Expected cached manifest at: {cachedManifest}");
    }

    // ── Add from local file ──────────────────────────────────────────────────

    [Fact]
    public async Task Add_from_local_file_registers()
    {
        var manager = this.CreateManager();
        var manifestFile = Path.Combine(this.fixtureDir, ".claude-plugin", "marketplace.json");

        var (ok, message) = await manager.AddAsync(manifestFile, CancellationToken.None);

        Assert.True(ok, message);
        var list = manager.List();
        Assert.Contains(list, x => x.Name == "fixture");
    }

    // ── Add duplicate ────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_duplicate_returns_error()
    {
        var manager = this.CreateManager();

        await manager.AddAsync(this.fixtureDir, CancellationToken.None);
        var (ok, message) = await manager.AddAsync(this.fixtureDir, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("already", message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_deletes_cache_and_entry()
    {
        var manager = this.CreateManager();
        await manager.AddAsync(this.fixtureDir, CancellationToken.None);

        var (ok, message) = manager.Remove("fixture");

        Assert.True(ok, message);
        Assert.Empty(manager.List());
        var cacheDir = Path.Combine(this.tempDir, "marketplaces", "fixture");
        Assert.False(Directory.Exists(cacheDir));
    }

    // ── GetPlugins ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPlugins_returns_manifest_entries()
    {
        var manager = this.CreateManager();
        await manager.AddAsync(this.fixtureDir, CancellationToken.None);

        var (ok, plugins, message) = await manager.GetPluginsAsync("fixture", CancellationToken.None);

        Assert.True(ok, message);
        Assert.Single(plugins);
        Assert.Equal("demo", plugins[0].Name);
    }

    // ── InstallPlugin from local source ──────────────────────────────────────

    [Fact]
    public async Task InstallPlugin_from_local_source_installs()
    {
        var manager = this.CreateManager();
        await manager.AddAsync(this.fixtureDir, CancellationToken.None);

        var (ok, message) = await manager.InstallPluginAsync("fixture", "demo", CancellationToken.None);

        Assert.True(ok, message);
        var pluginJson = Path.Combine(this.tempDir, "demo", "plugin.json");
        Assert.True(File.Exists(pluginJson), $"Expected plugin.json at: {pluginJson}");
    }

    // ── InstallPlugin unknown plugin ─────────────────────────────────────────

    [Fact]
    public async Task InstallPlugin_unknown_plugin_returns_error()
    {
        var manager = this.CreateManager();
        await manager.AddAsync(this.fixtureDir, CancellationToken.None);

        var (ok, message) = await manager.InstallPluginAsync("fixture", "nonexistent", CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("nonexistent", message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Add unrecognized input ────────────────────────────────────────────────

    [Fact]
    public async Task Add_unrecognized_input_returns_error()
    {
        var manager = this.CreateManager();

        var (ok, message) = await manager.AddAsync("not a marketplace", CancellationToken.None);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    // ── Remove unknown ───────────────────────────────────────────────────────

    [Fact]
    public void Remove_unknown_returns_error()
    {
        var manager = this.CreateManager();

        var (ok, message) = manager.Remove("nonexistent");

        Assert.False(ok);
        Assert.Contains("nonexistent", message, StringComparison.OrdinalIgnoreCase);
    }

    // ── GetPlugins unknown ───────────────────────────────────────────────────

    [Fact]
    public async Task GetPlugins_unknown_returns_error()
    {
        var manager = this.CreateManager();

        var (ok, plugins, message) = await manager.GetPluginsAsync("nonexistent", CancellationToken.None);

        Assert.False(ok);
        Assert.Empty(plugins);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    // ── Add git URL offline (graceful failure, no throw) ─────────────────────

    [Fact]
    public async Task Add_from_git_offline_returns_error_without_throwing()
    {
        var manager = this.CreateManager();

        // Use a bounded cancellation to ensure the test can't hang even if the
        // implementation's own timeout doesn't fire (extra safety net only).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var (ok, message) = await manager.AddAsync("https://localhost:9/none.git", cts.Token);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    // ── CRITICAL #1: relative dot-slash source installs locally ─────────────

    [Fact]
    public async Task InstallPlugin_with_relative_dot_slash_source_installs_locally()
    {
        // Fixture: a marketplace whose plugin source is "./demo" (dot-slash relative).
        // Before the fix this falls into the GitHub-shorthand branch and tries a git clone.
        var dotSlashFixtureDir = Path.Combine(this.tempDir, "fx-dotslash");
        var dotSlashPluginDir = Path.Combine(dotSlashFixtureDir, ".claude-plugin");
        Directory.CreateDirectory(dotSlashPluginDir);

        File.WriteAllText(
            Path.Combine(dotSlashPluginDir, "marketplace.json"),
            """
            {
              "name": "dotslash-market",
              "plugins": [
                { "name": "demo", "source": "./demo" }
              ]
            }
            """);

        var demoDir = Path.Combine(dotSlashFixtureDir, "demo");
        Directory.CreateDirectory(demoDir);
        File.WriteAllText(
            Path.Combine(demoDir, "plugin.json"),
            """{"name":"demo","version":"1.0.0","description":"Demo"}""");

        var manager = this.CreateManager();
        var (addOk, addMessage) = await manager.AddAsync(dotSlashFixtureDir, CancellationToken.None);
        Assert.True(addOk, addMessage);

        var (ok, message) = await manager.InstallPluginAsync("dotslash-market", "demo", CancellationToken.None);

        Assert.True(ok, message);
        var pluginJson = Path.Combine(this.tempDir, "demo", "plugin.json");
        Assert.True(File.Exists(pluginJson), $"Expected plugin.json at: {pluginJson}");
    }
}
