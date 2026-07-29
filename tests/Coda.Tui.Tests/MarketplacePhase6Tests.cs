using System.Text.Json;
using Coda.Tui.Commands;
using Coda.Tui.Plugins;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

// ============================================================================
// Test 1 — SHA validation
// A 40-hex sha is accepted; a 7-char abbreviation and a non-hex string are rejected.
// ============================================================================

public sealed class MarketplaceShaValidationTests
{
    [Theory]
    [InlineData("da39a3ee5e6b4b0d3255bfef95601890afd80709", true)]  // SHA1 of empty — 40 hex
    [InlineData("0000000000000000000000000000000000000000", true)]  // 40 zeros
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", true)]  // 40 uppercase hex
    [InlineData("da39a3e", false)]                                  // 7-char abbreviation
    [InlineData("da39a3ee5e6b4b0d3255bfef95601890afd8070", false)] // 39 chars
    [InlineData("da39a3ee5e6b4b0d3255bfef95601890afd807091", false)] // 41 chars
    [InlineData("ghijklmnopqrstuvwxyzabcdefghijklmnopqrst", false)] // 40 non-hex chars
    [InlineData("da39a3ee5e6b4b0d3255bfef95601890afd8070x", false)] // trailing non-hex
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsValidSha_accepts_40hex_rejects_others(string sha, bool expected)
    {
        Assert.Equal(expected, MarketplaceNameValidator.IsValidSha(sha));
    }

    [Fact]
    public void GithubSource_with_valid_sha_accepted()
    {
        const string sha = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
        var source = new GithubSource("owner/repo", "main", null, sha);
        var error = MarketplaceNameValidator.ValidateSourceSha(source);
        Assert.Null(error);
    }

    [Fact]
    public void GithubSource_with_abbreviated_sha_rejected()
    {
        const string sha = "da39a3e"; // 7-char — not collision-safe
        var source = new GithubSource("owner/repo", "main", null, sha);
        var error = MarketplaceNameValidator.ValidateSourceSha(source);
        Assert.NotNull(error);
        Assert.Contains("40", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitSource_with_non_hex_sha_rejected()
    {
        const string sha = "ghijklmnopqrstuvwxyz1234567890abcdef1234"; // non-hex chars
        var source = new GitSource("https://example.com/repo.git", null, null, sha);
        var error = MarketplaceNameValidator.ValidateSourceSha(source);
        Assert.NotNull(error);
    }
}

// ============================================================================
// Test 2 — SHA install recording
// Install with a pinned SHA records it as Commit; without one, Commit is null for local.
// ============================================================================

public sealed class MarketplaceShaInstallTests : IDisposable
{
    private readonly string tempDir;
    private readonly string fixtureDir;

    private const string ValidSha = "da39a3ee5e6b4b0d3255bfef95601890afd80709";

    public MarketplaceShaInstallTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-sha-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);

        this.fixtureDir = Path.Combine(this.tempDir, "fixture-src");
        Directory.CreateDirectory(this.fixtureDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    private void SetupMarketplace(string sha)
    {
        var claudePluginDir = Path.Combine(this.fixtureDir, ".claude-plugin");
        Directory.CreateDirectory(claudePluginDir);
        File.WriteAllText(
            Path.Combine(claudePluginDir, "marketplace.json"),
            $$"""
            {
              "name": "sha-fixture",
              "metadata": { "pluginRoot": "plugins" },
              "plugins": [
                {
                  "name": "pinned",
                  "source": { "source": "directory", "path": "pinned", "sha": "{{sha}}" },
                  "version": "1.0.0"
                }
              ]
            }
            """);

        var pluginDir = Path.Combine(this.fixtureDir, "plugins", "pinned");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name":"pinned","version":"1.0.0","description":"pinned plugin"}""");
    }

    private void SetupMarketplaceNoSha()
    {
        var claudePluginDir = Path.Combine(this.fixtureDir, ".claude-plugin");
        Directory.CreateDirectory(claudePluginDir);
        File.WriteAllText(
            Path.Combine(claudePluginDir, "marketplace.json"),
            """
            {
              "name": "no-sha-fixture",
              "metadata": { "pluginRoot": "plugins" },
              "plugins": [
                {
                  "name": "unpinned",
                  "source": { "source": "directory", "path": "unpinned" },
                  "version": "1.0.0"
                }
              ]
            }
            """);

        var pluginDir = Path.Combine(this.fixtureDir, "plugins", "unpinned");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name":"unpinned","version":"1.0.0","description":"unpinned plugin"}""");
    }

    [Fact]
    public async Task Install_with_pinned_sha_records_sha_as_commit()
    {
        this.SetupMarketplace(ValidSha);

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(this.fixtureDir, CancellationToken.None);
        var (ok, message) = await manager.InstallPluginAsync("sha-fixture", "pinned", CancellationToken.None);

        Assert.True(ok, message);

        // State must be readable via a store at the coda root (parent of plugins dir),
        // not from the plugins subdir itself — the shared store lives at codaDir.
        var codaDir = Path.GetDirectoryName(userPluginsDir)!;
        var stateStore = new PluginStateStore(codaDir);
        var info = stateStore.GetInstalledInfo("pinned");
        Assert.NotNull(info);
        Assert.Equal(ValidSha, info.Commit);
        Assert.Equal("sha-fixture", info.Marketplace);
    }

    [Fact]
    public async Task Install_without_sha_records_null_commit_for_local_source()
    {
        this.SetupMarketplaceNoSha();

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins2");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(this.fixtureDir, CancellationToken.None);
        var (ok, message) = await manager.InstallPluginAsync("no-sha-fixture", "unpinned", CancellationToken.None);

        Assert.True(ok, message);

        // Must read via the shared coda root store, not from the plugins subdir.
        var codaDir = Path.GetDirectoryName(userPluginsDir)!;
        var stateStore = new PluginStateStore(codaDir);
        var info = stateStore.GetInstalledInfo("unpinned");
        Assert.NotNull(info);
        Assert.Null(info.Commit); // no SHA pinned, no git to resolve
        Assert.Equal("no-sha-fixture", info.Marketplace);
    }
}

// ============================================================================
// Test 3 — Reserved names
// Blocked: exact match, case, separator, and confusable lookalikes.
// ============================================================================

public sealed class MarketplaceReservedNameTests
{
    [Theory]
    [InlineData("coda-marketplace")]
    [InlineData("CODA-MARKETPLACE")]          // case difference
    [InlineData("coda_marketplace")]           // underscore separator
    [InlineData("coda.marketplace")]           // dot separator
    [InlineData("codamarketplace")]            // no separator
    [InlineData("c0da-marketplace")]           // confusable 0 → o
    [InlineData("coda-plugins")]
    [InlineData("CODA-PLUGINS")]
    [InlineData("coda_plugins")]
    [InlineData("official-coda-plugins")]
    [InlineData("offlclal-coda-plug1ns")]      // confusable i/l → l
    [InlineData("coda-official")]
    [InlineData("C0DA-0FFICIAL")]              // confusable zeros
    [InlineData("c\u043Eda-official")]         // Cyrillic о (U+043E) in "coda"
    [InlineData("\u0441\u043Eda-official")]    // Cyrillic с,о then "da-official"
    [InlineData("coda-official ")]             // trailing space
    [InlineData(" coda-official")]             // leading space
    [InlineData("coda\u200Bofficial")]         // zero-width space between words
    public void CheckReserved_blocks_reserved_and_lookalikes(string name)
    {
        var reason = MarketplaceNameValidator.CheckReserved(name);
        Assert.NotNull(reason);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData("my-marketplace")]
    [InlineData("community-plugins")]
    [InlineData("awesome-tools")]
    [InlineData("fixture")]
    [InlineData("team-plugins")]
    public void CheckReserved_allows_non_reserved_names(string name)
    {
        var reason = MarketplaceNameValidator.CheckReserved(name);
        Assert.Null(reason);
    }

    [Fact]
    public async Task Add_reserved_name_marketplace_is_refused()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"coda-resv-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            // Create a source directory whose manifest.json declares a reserved name
            var sourceDir = Path.Combine(tempDir, "malicious");
            Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
            File.WriteAllText(
                Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
                """{"name": "coda-official", "plugins": []}""");

            var manager = new MarketplaceManager(Path.Combine(tempDir, "plugins"));
            var (ok, message) = await manager.AddAsync(sourceDir, CancellationToken.None);

            Assert.False(ok);
            Assert.Contains("reserved", message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}

// ============================================================================
// Test 4 — Previously-added marketplace now reserved, blocked on load
// ============================================================================

public sealed class MarketplaceReservedOnLoadTests : IDisposable
{
    private readonly string tempDir;

    public MarketplaceReservedOnLoadTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-resload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Previously_added_reserved_name_is_blocked_on_load()
    {
        // Bypass the Add() validator by writing directly to the JSON file
        File.WriteAllText(
            Path.Combine(this.tempDir, "known_marketplaces.json"),
            """
            {
              "coda-official": {
                "source": { "kind": "directory", "path": "/some/path" },
                "installLocation": "/some/install",
                "lastUpdated": "2026-01-01T00:00:00Z"
              },
              "safe-market": {
                "source": { "kind": "directory", "path": "/safe/path" },
                "installLocation": "/safe/install",
                "lastUpdated": "2026-01-01T00:00:00Z"
              }
            }
            """);

        var store = new KnownMarketplacesStore(this.tempDir);
        var loaded = store.ListWithBlockStatus();

        var blocked = loaded.First(x => x.Name == "coda-official");
        Assert.NotNull(blocked.BlockReason);
        Assert.Contains("reserved", blocked.BlockReason, StringComparison.OrdinalIgnoreCase);

        var safe = loaded.First(x => x.Name == "safe-market");
        Assert.Null(safe.BlockReason);
    }

    [Fact]
    public void Blocked_marketplace_not_returned_by_manager_list()
    {
        // Write reserved marketplace directly to JSON
        File.WriteAllText(
            Path.Combine(this.tempDir, "known_marketplaces.json"),
            """
            {
              "coda-official": {
                "source": { "kind": "directory", "path": "/some/path" },
                "installLocation": "/some/install",
                "lastUpdated": "2026-01-01T00:00:00Z"
              }
            }
            """);

        var manager = new MarketplaceManager(this.tempDir);
        var list = manager.List();

        // The blocked marketplace should appear in the list but with a block reason
        var blocked = list.FirstOrDefault(x => x.Name == "coda-official");
        Assert.NotNull(blocked.BlockReason);
    }
}

// ============================================================================
// Test 5 — Relocation: renames and migratedTo
// ============================================================================

public sealed class MarketplaceRelocationTests : IDisposable
{
    private readonly string tempDir;

    public MarketplaceRelocationTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-relo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Renames_maps_old_plugin_name_to_new_name()
    {
        var json = """
            {
              "name": "test-market",
              "plugins": [
                { "name": "new-plugin", "source": "https://github.com/owner/new-plugin.git" }
              ],
              "renames": {
                "old-plugin": "new-plugin"
              }
            }
            """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.True(manifest!.GetRenames().ContainsKey("old-plugin"));
        Assert.Equal("new-plugin", manifest.GetRenames()["old-plugin"]);
    }

    [Fact]
    public void Renames_null_value_retires_plugin()
    {
        var json = """
            {
              "name": "test-market",
              "plugins": [],
              "renames": {
                "deprecated-plugin": null
              }
            }
            """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.True(manifest!.GetRenames().ContainsKey("deprecated-plugin"));
        Assert.Null(manifest.GetRenames()["deprecated-plugin"]);
    }

    [Fact]
    public void MigratedTo_parsed_from_plugin_entry()
    {
        var json = """
            {
              "name": "test-market",
              "plugins": [
                {
                  "name": "moved-plugin",
                  "source": "https://github.com/owner/old.git",
                  "migratedTo": "https://github.com/owner/new.git"
                }
              ]
            }
            """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(error);
        Assert.NotNull(manifest);
        var plugin = Assert.Single(manifest!.Plugins);
        Assert.Equal("https://github.com/owner/new.git", plugin.MigratedTo);
    }

    [Fact]
    public async Task Refresh_applies_renames_in_diff()
    {
        // Set up a marketplace with an old plugin name
        var sourceDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "rename-market",
              "plugins": [
                { "name": "old-name", "source": "https://example.com/old.git", "version": "1.0.0" }
              ]
            }
            """);

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);

        // Update source to use renames
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "rename-market",
              "plugins": [
                { "name": "new-name", "source": "https://example.com/new.git", "version": "2.0.0" }
              ],
              "renames": { "old-name": "new-name" }
            }
            """);

        var results = await manager.RefreshAsync("rename-market", CancellationToken.None);

        Assert.Single(results);
        var result = results[0];
        Assert.True(result.Ok, result.Error ?? "unexpected failure");
        Assert.NotNull(result.Diff);

        // A rename must appear in Diff.Renamed, not as a remove + add.
        Assert.NotNull(result.Diff!.Renamed);
        Assert.Contains(result.Diff.Renamed!, r => r.OldName == "old-name" && r.NewName == "new-name");
        Assert.DoesNotContain("old-name", result.Diff.Removed);
        Assert.DoesNotContain("new-name", result.Diff.Added);
    }
}

// ============================================================================
// Test 6 — Blocked redirect: rename/migration targeting a blocked source is refused
// ============================================================================

public sealed class MarketplaceBlockedRedirectTests : IDisposable
{
    private readonly string tempDir;

    public MarketplaceBlockedRedirectTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-blkrdr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task MigratedTo_targeting_reserved_marketplace_is_refused()
    {
        // Set up a plugin with migratedTo pointing to a local dir with a reserved name
        var reservedTargetDir = Path.Combine(this.tempDir, "reserved-target");
        Directory.CreateDirectory(Path.Combine(reservedTargetDir, ".claude-plugin"));
        File.WriteAllText(
            Path.Combine(reservedTargetDir, ".claude-plugin", "marketplace.json"),
            """{"name": "coda-official", "plugins": [{"name":"x","source":"./x"}]}""");

        var sourceDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            $$"""
            {
              "name": "legit-market",
              "plugins": [
                {
                  "name": "migrating",
                  "source": "https://example.com/old.git",
                  "migratedTo": "{{reservedTargetDir.Replace("\\", "\\\\")}}"
                }
              ]
            }
            """);

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);

        var results = await manager.RefreshAsync("legit-market", CancellationToken.None);

        Assert.Single(results);
        var result = results[0];
        Assert.True(result.Ok, result.Error ?? "unexpected failure"); // refresh itself succeeds
        // The blocked migration must be surfaced in BlockedMigrations, not silently discarded.
        Assert.NotNull(result.BlockedMigrations);
        Assert.Contains("migrating", result.BlockedMigrations, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Renames_with_reserved_new_name_is_blocked_during_validation()
    {
        // The renames field maps an old plugin to a new name that matches a reserved marketplace name
        var json = """
            {
              "name": "legit-market",
              "plugins": [],
              "renames": { "old-plugin": "coda-official" }
            }
            """;

        var (manifest, _) = MarketplaceManifestParser.Parse(json);
        Assert.NotNull(manifest);

        // Validate renames against reserved names
        var error = MarketplaceNameValidator.ValidateRenames(manifest!.GetRenames());
        Assert.NotNull(error);
        Assert.Contains("reserved", error, StringComparison.OrdinalIgnoreCase);
    }
}

// ============================================================================
// Test 7 — Refresh
// Updates lastUpdated, reports added/removed/changed; one failure doesn't abort rest.
// ============================================================================

public sealed class MarketplaceRefreshTests : IDisposable
{
    private readonly string tempDir;

    public MarketplaceRefreshTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Refresh_updates_lastUpdated_and_reports_diff()
    {
        var sourceDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));

        // Initial manifest: 2 plugins
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "refresh-market",
              "plugins": [
                { "name": "alpha", "source": "https://example.com/alpha.git", "version": "1.0.0" },
                { "name": "beta",  "source": "https://example.com/beta.git",  "version": "2.0.0" }
              ]
            }
            """);

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);

        var originalEntry = manager.List().First(x => x.Name == "refresh-market");
        var originalLastUpdated = originalEntry.Entry.LastUpdated;

        // Give time so the timestamp actually changes
        await Task.Delay(10);

        // Update the source: remove beta, add gamma, bump alpha's version
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "refresh-market",
              "plugins": [
                { "name": "alpha", "source": "https://example.com/alpha.git", "version": "1.1.0" },
                { "name": "gamma", "source": "https://example.com/gamma.git", "version": "3.0.0" }
              ]
            }
            """);

        var results = await manager.RefreshAsync("refresh-market", CancellationToken.None);

        Assert.Single(results);
        var result = results[0];
        Assert.True(result.Ok, result.Error ?? "unexpected failure");
        Assert.NotNull(result.Diff);
        Assert.Contains("gamma", result.Diff!.Added);
        Assert.Contains("beta", result.Diff.Removed);
        Assert.Contains(result.Diff.VersionChanged, vc => vc.Name == "alpha" && vc.NewVersion == "1.1.0");

        // lastUpdated should have changed
        var updatedEntry = manager.List().First(x => x.Name == "refresh-market");
        Assert.NotEqual(originalLastUpdated, updatedEntry.Entry.LastUpdated);
    }

    [Fact]
    public async Task Refresh_all_one_failure_does_not_abort_others()
    {
        var goodSourceDir = Path.Combine(this.tempDir, "good-src");
        Directory.CreateDirectory(Path.Combine(goodSourceDir, ".claude-plugin"));
        File.WriteAllText(
            Path.Combine(goodSourceDir, ".claude-plugin", "marketplace.json"),
            """{"name": "good-market", "plugins": [{"name":"p","source":"./p","version":"1.0.0"}]}""");

        var badSourceDir = Path.Combine(this.tempDir, "bad-src");

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins");
        var manager = new MarketplaceManager(userPluginsDir);

        await manager.AddAsync(goodSourceDir, CancellationToken.None);

        // Manually register a "bad" marketplace pointing to a non-existent directory
        var store = new KnownMarketplacesStore(userPluginsDir);
        store.Add("bad-market", new KnownMarketplaceEntry(
            new LocalDirectorySource(badSourceDir),
            Path.Combine(userPluginsDir, "marketplaces", "bad-market"),
            DateTimeOffset.UtcNow.ToString("O")));

        // Refresh all — bad-market should fail, good-market should succeed
        var results = await manager.RefreshAsync(null, CancellationToken.None);

        Assert.Equal(2, results.Count);
        var goodResult = results.First(r => r.Name == "good-market");
        var badResult = results.First(r => r.Name == "bad-market");

        Assert.True(goodResult.Ok, goodResult.Error ?? "good market should succeed");
        Assert.False(badResult.Ok);
        Assert.NotNull(badResult.Error);
    }

    [Fact]
    public async Task Refresh_single_named_market_only_refreshes_that_one()
    {
        var sourceDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """{"name": "single-market", "plugins": [{"name":"p","source":"./p","version":"1.0.0"}]}""");

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);

        var results = await manager.RefreshAsync("single-market", CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("single-market", results[0].Name);
    }
}

// ============================================================================
// Test 8 — Search
// Matches name, description, category, tags; name prefix ranks above description;
// shows source and installed state.
// ============================================================================

public sealed class MarketplaceSearchTests : IDisposable
{
    private readonly string tempDir;
    private readonly string userPluginsDir;

    public MarketplaceSearchTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.userPluginsDir = Path.Combine(this.tempDir, "user-plugins");
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    private async Task<MarketplaceManager> SetupMarketplaceAsync()
    {
        var sourceDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "search-market",
              "plugins": [
                {
                  "name": "formatter",
                  "source": "https://example.com/formatter.git",
                  "description": "Formats code nicely",
                  "category": "tooling",
                  "tags": ["format", "lint"],
                  "version": "1.0.0"
                },
                {
                  "name": "linter",
                  "source": "https://example.com/linter.git",
                  "description": "A formatter-compatible linter",
                  "category": "analysis",
                  "tags": ["lint", "static-analysis"],
                  "version": "2.0.0"
                },
                {
                  "name": "debugger",
                  "source": "https://example.com/debugger.git",
                  "description": "Step-through debugging support",
                  "category": "debugging",
                  "tags": ["debug"],
                  "version": "1.5.0"
                }
              ]
            }
            """);

        var manager = new MarketplaceManager(this.userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);
        return manager;
    }

    [Fact]
    public async Task Search_matches_by_name_prefix()
    {
        var manager = await this.SetupMarketplaceAsync();

        var results = await manager.SearchAsync("form", CancellationToken.None);

        Assert.Contains(results, r => r.Entry.Name == "formatter");
        // formatter starts with "form" → rank 0
        var formatter = results.First(r => r.Entry.Name == "formatter");
        Assert.Equal(0, formatter.Rank);
    }

    [Fact]
    public async Task Search_matches_by_description()
    {
        var manager = await this.SetupMarketplaceAsync();

        var results = await manager.SearchAsync("step-through", CancellationToken.None);

        Assert.Contains(results, r => r.Entry.Name == "debugger");
    }

    [Fact]
    public async Task Search_matches_by_category()
    {
        var manager = await this.SetupMarketplaceAsync();

        var results = await manager.SearchAsync("analysis", CancellationToken.None);

        Assert.Contains(results, r => r.Entry.Name == "linter");
    }

    [Fact]
    public async Task Search_matches_by_tags()
    {
        var manager = await this.SetupMarketplaceAsync();

        var results = await manager.SearchAsync("static-analysis", CancellationToken.None);

        Assert.Contains(results, r => r.Entry.Name == "linter");
    }

    [Fact]
    public async Task Search_name_prefix_ranks_above_description_match()
    {
        var manager = await this.SetupMarketplaceAsync();

        // "format" is a prefix of "formatter" AND appears in linter's description
        var results = await manager.SearchAsync("format", CancellationToken.None);

        var formatter = results.FirstOrDefault(r => r.Entry.Name == "formatter");
        var linter = results.FirstOrDefault(r => r.Entry.Name == "linter");

        Assert.NotNull(formatter);
        Assert.NotNull(linter);
        // formatter (name-prefix rank 0) should rank better than linter (description rank 2)
        Assert.True(formatter!.Rank < linter!.Rank, $"formatter rank {formatter.Rank} should beat linter rank {linter.Rank}");
    }

    [Fact]
    public async Task Search_shows_marketplace_source()
    {
        var manager = await this.SetupMarketplaceAsync();

        var results = await manager.SearchAsync("formatter", CancellationToken.None);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.NotEmpty(r.MarketplaceName));
        Assert.All(results, r => Assert.Equal("search-market", r.MarketplaceName));
    }

    [Fact]
    public async Task Search_shows_installed_state()
    {
        var manager = await this.SetupMarketplaceAsync();

        // Manually "install" the formatter by creating its directory
        var formatterDir = Path.Combine(this.userPluginsDir, "formatter");
        Directory.CreateDirectory(formatterDir);
        File.WriteAllText(
            Path.Combine(formatterDir, "plugin.json"),
            """{"name":"formatter","version":"1.0.0"}""");

        var results = await manager.SearchAsync("formatter", CancellationToken.None);

        var formatterResult = results.FirstOrDefault(r => r.Entry.Name == "formatter");
        Assert.NotNull(formatterResult);
        Assert.True(formatterResult!.IsInstalled);

        var debuggerResult = results.FirstOrDefault(r => r.Entry.Name == "debugger");
        // debugger is not installed — won't be in results for "formatter" query
        // Search for "debug" to find it
        var debugResults = await manager.SearchAsync("debug", CancellationToken.None);
        var debuggerFound = debugResults.FirstOrDefault(r => r.Entry.Name == "debugger");
        Assert.NotNull(debuggerFound);
        Assert.False(debuggerFound!.IsInstalled);
    }
}

// ============================================================================
// Test 9 — Remove refuses with dependents; --force proceeds
// ============================================================================

public sealed class MarketplaceRemoveDependentsTests : IDisposable
{
    private readonly string tempDir;

    public MarketplaceRemoveDependentsTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-rmvdep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    private async Task<(MarketplaceManager Manager, string UserPluginsDir)> SetupWithInstalledPluginAsync()
    {
        var sourceDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        Directory.CreateDirectory(Path.Combine(sourceDir, "plugins", "my-tool"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "dep-market",
              "metadata": { "pluginRoot": "plugins" },
              "plugins": [
                { "name": "my-tool", "source": "my-tool", "version": "1.0.0" }
              ]
            }
            """);
        File.WriteAllText(
            Path.Combine(sourceDir, "plugins", "my-tool", "plugin.json"),
            """{"name":"my-tool","version":"1.0.0"}""");

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);
        await manager.InstallPluginAsync("dep-market", "my-tool", CancellationToken.None);

        return (manager, userPluginsDir);
    }

    [Fact]
    public async Task Remove_with_dependents_refuses_and_lists_them()
    {
        var (manager, _) = await this.SetupWithInstalledPluginAsync();

        var (ok, message, dependents) = manager.Remove("dep-market");

        Assert.False(ok);
        Assert.Contains("my-tool", message);
        Assert.Contains("my-tool", dependents);
        Assert.Contains("--force", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remove_force_removes_despite_dependents()
    {
        var (manager, _) = await this.SetupWithInstalledPluginAsync();

        var (ok, message, _) = manager.Remove("dep-market", force: true);

        Assert.True(ok, message);
        Assert.DoesNotContain(manager.List(), x => x.Name == "dep-market");
    }

    [Fact]
    public async Task Remove_no_dependents_succeeds_without_force()
    {
        var sourceDir = Path.Combine(this.tempDir, "no-dep-src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """{"name": "no-dep-market", "plugins": [{"name":"orphan","source":"./orphan"}]}""");

        var userPluginsDir = Path.Combine(this.tempDir, "no-dep-plugins");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);
        // Do NOT install any plugin

        var (ok, message, dependents) = manager.Remove("no-dep-market");

        Assert.True(ok, message);
        Assert.Empty(dependents);
    }
}

// ============================================================================
// Test 10 — Existing behavior unchanged
// All previous capabilities work when none of the new features are used.
// ============================================================================

public sealed class MarketplaceExistingBehaviorPhase6Tests : IDisposable
{
    private readonly string tempDir;
    private readonly string userPluginsDir;
    private readonly string fixtureDir;

    public MarketplaceExistingBehaviorPhase6Tests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.userPluginsDir = Path.Combine(this.tempDir, "user-plugins");

        this.fixtureDir = Path.Combine(this.tempDir, "fixture-mp");
        var claudePluginDir = Path.Combine(this.fixtureDir, ".claude-plugin");
        Directory.CreateDirectory(claudePluginDir);
        File.WriteAllText(
            Path.Combine(claudePluginDir, "marketplace.json"),
            """
            {
              "name": "legacy-test",
              "metadata": { "pluginRoot": "plugins" },
              "plugins": [
                {
                  "name": "classic",
                  "source": "classic",
                  "description": "Classic plugin",
                  "version": "1.0.0",
                  "category": "tools",
                  "tags": ["classic"]
                }
              ]
            }
            """);

        Directory.CreateDirectory(Path.Combine(this.fixtureDir, "plugins", "classic"));
        File.WriteAllText(
            Path.Combine(this.fixtureDir, "plugins", "classic", "plugin.json"),
            """{"name":"classic","version":"1.0.0","description":"Classic plugin"}""");
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Legacy_add_list_browse_install_remove_unaffected()
    {
        var manager = new MarketplaceManager(this.userPluginsDir);

        // Add
        var (addOk, addMsg) = await manager.AddAsync(this.fixtureDir, CancellationToken.None);
        Assert.True(addOk, addMsg);

        // List
        var list = manager.List();
        Assert.Contains(list, x => x.Name == "legacy-test");

        // Browse
        var (browseOk, plugins, browseMsg) = await manager.GetPluginsAsync("legacy-test", CancellationToken.None);
        Assert.True(browseOk, browseMsg);
        Assert.Contains(plugins, p => p.Name == "classic");

        // Install
        var (installOk, installMsg) = await manager.InstallPluginAsync("legacy-test", "classic", CancellationToken.None);
        Assert.True(installOk, installMsg);
        Assert.True(File.Exists(Path.Combine(this.userPluginsDir, "classic", "plugin.json")));

        // Remove marketplace (no force — no dependent check concerns here because
        // we're calling with force=false but the plugin is installed. So we use force: true
        // to match the new semantics without breaking the legacy flow)
        var (removeOk, removeMsg, _) = manager.Remove("legacy-test", force: true);
        Assert.True(removeOk, removeMsg);
        Assert.DoesNotContain(manager.List(), x => x.Name == "legacy-test");
    }

    [Fact]
    public async Task Command_search_subcommand_reaches_production_path()
    {
        // Add the fixture marketplace via command
        var command = new MarketplaceCommand(this.userPluginsDir);
        var (addConsole, addCtx) = this.BuildContext();
        await command.ExecuteAsync(addCtx, ["add", this.fixtureDir], CancellationToken.None);

        // Search via command
        var (searchConsole, searchCtx) = this.BuildContext();
        var result = await command.ExecuteAsync(searchCtx, ["search", "classic"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("classic", searchConsole.Output);
    }

    [Fact]
    public async Task Command_refresh_subcommand_reaches_production_path()
    {
        var command = new MarketplaceCommand(this.userPluginsDir);
        var (addConsole, addCtx) = this.BuildContext();
        await command.ExecuteAsync(addCtx, ["add", this.fixtureDir], CancellationToken.None);

        var (refreshConsole, refreshCtx) = this.BuildContext();
        var result = await command.ExecuteAsync(refreshCtx, ["refresh", "legacy-test"], CancellationToken.None);

        Assert.False(result.ShouldExit);
    }

    [Fact]
    public async Task Command_remove_force_reaches_production_path()
    {
        var command = new MarketplaceCommand(this.userPluginsDir);
        var (addConsole, addCtx) = this.BuildContext();
        await command.ExecuteAsync(addCtx, ["add", this.fixtureDir], CancellationToken.None);

        var (installConsole, installCtx) = this.BuildContext();
        await command.ExecuteAsync(installCtx, ["install", "classic", "legacy-test"], CancellationToken.None);

        // remove without --force should fail because plugin is installed
        var (removeConsole, removeCtx) = this.BuildContext();
        await command.ExecuteAsync(removeCtx, ["remove", "legacy-test"], CancellationToken.None);
        Assert.Contains("classic", removeConsole.Output); // shows dependent list

        // remove with --force should succeed
        var (forceConsole, forceCtx) = this.BuildContext();
        var result = await command.ExecuteAsync(forceCtx, ["remove", "--force", "legacy-test"], CancellationToken.None);
        Assert.False(result.ShouldExit);
    }

    private (TestConsole Console, CommandContext Context) BuildContext()
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude });

        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai", this.tempDir);
        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new HelpCommand(), new SkillsCommand(), new SkillCommand(),
            new PluginsCommand(), new PluginCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}

// ============================================================================
// Review-fix tests — C1: state store must be at coda root, not plugins subdir
// ============================================================================

public sealed class MarketplaceStateStorePathTests : IDisposable
{
    private readonly string tempDir;

    public MarketplaceStateStorePathTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-sstpath-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Install_writes_plugin_state_to_coda_root_not_plugins_subdir()
    {
        // Arrange: userPluginsDir is one level below the coda root (codaDir).
        var userPluginsDir = Path.Combine(this.tempDir, "plugins");
        var codaDir = this.tempDir;

        var sourceDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        Directory.CreateDirectory(Path.Combine(sourceDir, "plugs", "state-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "state-mkt",
              "metadata": { "pluginRoot": "plugs" },
              "plugins": [
                { "name": "state-plugin", "source": "state-plugin" }
              ]
            }
            """);
        File.WriteAllText(
            Path.Combine(sourceDir, "plugs", "state-plugin", "plugin.json"),
            """{"name":"state-plugin","version":"1.0.0","description":"test"}""");

        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);
        var (ok, msg) = await manager.InstallPluginAsync("state-mkt", "state-plugin", CancellationToken.None);
        Assert.True(ok, msg);

        // State must be in the coda root, NOT in the plugins subdir.
        var correctStore = new PluginStateStore(codaDir);
        var info = correctStore.GetInstalledInfo("state-plugin");
        Assert.NotNull(info);
        Assert.Equal("state-mkt", info!.Marketplace);

        // plugin-state.json must NOT exist inside the plugins subdir.
        var wrongFile = Path.Combine(userPluginsDir, "plugin-state.json");
        Assert.False(File.Exists(wrongFile),
            "plugin-state.json must be in the coda root, not the plugins subdir.");
    }

    [Fact]
    public async Task PluginCommand_and_MarketplaceManager_share_the_same_state_file()
    {
        // A marketplace-installed plugin's state should be visible to /plugin update,
        // which reads from the coda root store (CommandContext.PluginState = PluginStateStore(codaDir)).
        var userPluginsDir = Path.Combine(this.tempDir, "plugins2");
        var codaDir = this.tempDir;

        var sourceDir = Path.Combine(this.tempDir, "src2");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        Directory.CreateDirectory(Path.Combine(sourceDir, "p", "shared-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "shared-mkt",
              "metadata": { "pluginRoot": "p" },
              "plugins": [
                { "name": "shared-plugin", "source": "shared-plugin" }
              ]
            }
            """);
        File.WriteAllText(
            Path.Combine(sourceDir, "p", "shared-plugin", "plugin.json"),
            """{"name":"shared-plugin","version":"1.0.0","description":"test"}""");

        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);
        var (ok, msg) = await manager.InstallPluginAsync("shared-mkt", "shared-plugin", CancellationToken.None);
        Assert.True(ok, msg);

        // The shared store at the coda root (what PluginCommand reads from context.PluginState)
        // must contain the marketplace-installed plugin's record.
        var sharedStore = new PluginStateStore(codaDir);
        var info = sharedStore.GetInstalledInfo("shared-plugin");
        Assert.NotNull(info);
        Assert.Equal("shared-mkt", info!.Marketplace);

        // Simulating what /plugin update does: reads installInfo from the shared store.
        // If the path was wrong it would return null and the update command would abort.
        Assert.NotNull(info.Version);
    }
}

// ============================================================================
// Review-fix tests — C2: SHA pin validation at install time
// ============================================================================

public sealed class MarketplaceGitShaInstallValidationTests : IDisposable
{
    private readonly string tempDir;

    public MarketplaceGitShaInstallValidationTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-gitshaval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task InstallPlugin_with_abbreviated_sha_in_git_source_is_refused()
    {
        const string badSha = "abc1234"; // 7 chars — not collision-safe
        var sourceDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            $$"""
            {
              "name": "sha-test-mkt",
              "plugins": [
                {
                  "name": "pinned-git",
                  "source": { "source": "git", "url": "https://example.com/repo.git", "sha": "{{badSha}}" }
                }
              ]
            }
            """);

        var userPluginsDir = Path.Combine(this.tempDir, "plugins");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);
        var (ok, message) = await manager.InstallPluginAsync("sha-test-mkt", "pinned-git", CancellationToken.None);

        Assert.False(ok, "Should reject an abbreviated SHA in the plugin source.");
        Assert.Contains("40", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallPlugin_with_garbage_sha_in_git_source_is_refused()
    {
        const string garbageSha = "not-a-sha-at-all-but-exactly-forty-chars!!";
        var sourceDir = Path.Combine(this.tempDir, "src2");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            $$"""
            {
              "name": "sha-garbage-mkt",
              "plugins": [
                {
                  "name": "bad-git",
                  "source": { "source": "git", "url": "https://example.com/repo.git", "sha": "{{garbageSha}}" }
                }
              ]
            }
            """);

        var userPluginsDir = Path.Combine(this.tempDir, "plugins2");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);
        var (ok, message) = await manager.InstallPluginAsync("sha-garbage-mkt", "bad-git", CancellationToken.None);

        Assert.False(ok, "Should reject a non-hex SHA in the plugin source.");
    }

    [Fact]
    public async Task InstallPlugin_local_source_records_source_local_and_no_giturl()
    {
        // A local-directory plugin source should record Source: "local" and no GitUrl.
        var sourceDir = Path.Combine(this.tempDir, "src3");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        Directory.CreateDirectory(Path.Combine(sourceDir, "plugs", "local-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "local-src-mkt",
              "metadata": { "pluginRoot": "plugs" },
              "plugins": [
                { "name": "local-plugin", "source": "local-plugin" }
              ]
            }
            """);
        File.WriteAllText(
            Path.Combine(sourceDir, "plugs", "local-plugin", "plugin.json"),
            """{"name":"local-plugin","version":"1.0.0","description":"test"}""");

        var userPluginsDir = Path.Combine(this.tempDir, "plugins3");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);
        var (ok, msg) = await manager.InstallPluginAsync("local-src-mkt", "local-plugin", CancellationToken.None);
        Assert.True(ok, msg);

        var codaDir = Path.GetDirectoryName(userPluginsDir)!;
        var stateStore = new PluginStateStore(codaDir);
        var info = stateStore.GetInstalledInfo("local-plugin");
        Assert.NotNull(info);
        Assert.Equal("local", info!.Source);
        Assert.Null(info.GitUrl);
    }
}

// ============================================================================
// Review-fix tests — I1: migratedTo targeting remote reserved names is blocked
// ============================================================================

public sealed class MarketplaceMigratedToRemoteTests : IDisposable
{
    private readonly string tempDir;

    public MarketplaceMigratedToRemoteTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-migremote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task MigratedTo_targeting_github_url_with_reserved_name_is_blocked()
    {
        // migratedTo points to a GitHub URL whose repo path contains a reserved name.
        var sourceDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "legit-remote-mkt",
              "plugins": [
                {
                  "name": "moving-plugin",
                  "source": "https://example.com/old.git",
                  "migratedTo": "coda-official/plugins"
                }
              ]
            }
            """);

        var userPluginsDir = Path.Combine(this.tempDir, "plugins");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);
        var results = await manager.RefreshAsync("legit-remote-mkt", CancellationToken.None);

        Assert.Single(results);
        var result = results[0];
        Assert.True(result.Ok, result.Error ?? "refresh itself should succeed");
        Assert.NotNull(result.BlockedMigrations);
        Assert.Contains("moving-plugin", result.BlockedMigrations!, StringComparer.OrdinalIgnoreCase);
    }
}

// ============================================================================
// Review-fix tests — I2: ValidateRenames called during refresh
// ============================================================================

public sealed class MarketplaceRenamesValidatedOnRefreshTests : IDisposable
{
    private readonly string tempDir;

    public MarketplaceRenamesValidatedOnRefreshTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-renval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Refresh_with_renames_targeting_reserved_name_fails()
    {
        var sourceDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """{"name": "ren-val-mkt", "plugins": [{"name":"p","source":"./p","version":"1.0.0"}]}""");

        var userPluginsDir = Path.Combine(this.tempDir, "plugins");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);

        // Update source to include renames pointing to a reserved target.
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "ren-val-mkt",
              "plugins": [{"name":"new-p","source":"./new-p","version":"2.0.0"}],
              "renames": { "p": "coda-official" }
            }
            """);

        var results = await manager.RefreshAsync("ren-val-mkt", CancellationToken.None);

        Assert.Single(results);
        Assert.False(results[0].Ok, "Refresh must fail when renames target a reserved name.");
        Assert.NotNull(results[0].Error);
        Assert.Contains("reserved", results[0].Error!, StringComparison.OrdinalIgnoreCase);
    }
}

// ============================================================================
// Review-fix tests — L1: duplicate plugin names in manifest don't throw
// ============================================================================

public sealed class MarketplaceDuplicatePluginNamesTests : IDisposable
{
    private readonly string tempDir;

    public MarketplaceDuplicatePluginNamesTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-dupnames-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Refresh_with_duplicate_plugin_names_in_manifest_does_not_throw()
    {
        var sourceDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """{"name":"dup-mkt","plugins":[{"name":"p","source":"./p","version":"1.0.0"}]}""");

        var userPluginsDir = Path.Combine(this.tempDir, "plugins");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);

        // Update source with duplicate plugin names (e.g., bad manifest).
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "dup-mkt",
              "plugins": [
                {"name":"p","source":"./p","version":"1.0.0"},
                {"name":"p","source":"./p","version":"1.1.0"}
              ]
            }
            """);

        // Must not throw ArgumentException from ToDictionary.
        var ex = await Record.ExceptionAsync(
            () => manager.RefreshAsync("dup-mkt", CancellationToken.None));
        Assert.Null(ex);
    }
}

// ============================================================================
// Review-fix tests — L2: @sha in input parser populates Sha field, not Ref
// ============================================================================

public sealed class MarketplaceInputParserShaTests
{
    private const string FullSha = "da39a3ee5e6b4b0d3255bfef95601890afd80709";

    [Fact]
    public void Parse_github_shorthand_with_full_sha_at_sign_populates_sha_field()
    {
        var (source, error) = MarketplaceInputParser.Parse($"owner/repo@{FullSha}");

        Assert.Null(error);
        var github = Assert.IsType<GithubSource>(source);
        Assert.Equal(FullSha, github.Sha);
        Assert.Null(github.Ref); // full SHA goes to Sha, not Ref
    }

    [Fact]
    public void Parse_github_shorthand_with_branch_ref_stays_in_ref_field()
    {
        var (source, error) = MarketplaceInputParser.Parse("owner/repo@main");

        Assert.Null(error);
        var github = Assert.IsType<GithubSource>(source);
        Assert.Equal("main", github.Ref);
        Assert.Null(github.Sha); // branch stays in Ref
    }

    [Fact]
    public void Parse_https_github_url_with_full_sha_fragment_populates_sha_field()
    {
        var (source, error) = MarketplaceInputParser.Parse(
            $"https://github.com/owner/repo.git#{FullSha}");

        Assert.Null(error);
        // GitHub URLs go through the GitSource path (has .git)
        var git = Assert.IsType<GitSource>(source);
        Assert.Equal(FullSha, git.Sha);
        Assert.Null(git.Ref);
    }
}

// ============================================================================
// Review-fix tests — L3: case-insensitive installed-name detection in search
// ============================================================================

public sealed class MarketplaceCaseInsensitiveInstalledTests : IDisposable
{
    private readonly string tempDir;

    public MarketplaceCaseInsensitiveInstalledTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-cicase-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Search_marks_plugin_as_installed_regardless_of_case_difference()
    {
        var sourceDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "ci-mkt",
              "plugins": [
                { "name": "MyPlugin", "source": "https://example.com/myplugin.git", "version": "1.0.0" }
              ]
            }
            """);

        var userPluginsDir = Path.Combine(this.tempDir, "plugins");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);

        // Plugin directory was installed with different capitalisation.
        var pluginDir = Path.Combine(userPluginsDir, "myplugin"); // lowercase on disk
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name":"myplugin","version":"1.0.0"}""");

        // Search should detect the installed directory case-insensitively.
        var results = await manager.SearchAsync("MyPlugin", CancellationToken.None);
        var match = results.FirstOrDefault(r =>
            string.Equals(r.Entry.Name, "MyPlugin", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(match);
        Assert.True(match!.IsInstalled,
            "Plugin should be marked as installed despite case difference in directory name.");
    }
}

// ============================================================================
// Review-fix tests — M2: dependent detection uses recorded provenance
// ============================================================================

public sealed class MarketplaceDependentProvenanceTests : IDisposable
{
    private readonly string tempDir;

    public MarketplaceDependentProvenanceTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-deprov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Remove_refuses_when_installed_plugin_has_marketplace_provenance_even_if_dropped_from_manifest()
    {
        var sourceDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".claude-plugin"));
        Directory.CreateDirectory(Path.Combine(sourceDir, "plugs", "prov-plugin"));
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """
            {
              "name": "prov-mkt",
              "metadata": { "pluginRoot": "plugs" },
              "plugins": [{ "name": "prov-plugin", "source": "prov-plugin", "version": "1.0.0" }]
            }
            """);
        File.WriteAllText(
            Path.Combine(sourceDir, "plugs", "prov-plugin", "plugin.json"),
            """{"name":"prov-plugin","version":"1.0.0","description":"test"}""");

        var userPluginsDir = Path.Combine(this.tempDir, "plugins");
        var manager = new MarketplaceManager(userPluginsDir);
        await manager.AddAsync(sourceDir, CancellationToken.None);
        var (installOk, installMsg) = await manager.InstallPluginAsync("prov-mkt", "prov-plugin", CancellationToken.None);
        Assert.True(installOk, installMsg);

        // Now update the marketplace manifest to DROP the plugin listing.
        File.WriteAllText(
            Path.Combine(sourceDir, ".claude-plugin", "marketplace.json"),
            """{"name": "prov-mkt", "plugins": []}""");
        await manager.RefreshAsync("prov-mkt", CancellationToken.None);

        // Plugin is still installed on disk with Marketplace="prov-mkt" in state store.
        // Remove must refuse because provenance says prov-plugin belongs to prov-mkt.
        var (ok, message, dependents) = manager.Remove("prov-mkt");
        Assert.False(ok,
            "Remove must refuse when an installed plugin's recorded Marketplace matches.");
        Assert.Contains("prov-plugin", dependents, StringComparer.OrdinalIgnoreCase);
    }
}

