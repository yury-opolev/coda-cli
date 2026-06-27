namespace Coda.Tui.Tests;

using Coda.Tui.Plugins;

public sealed class MarketplaceManifestParserTests
{
    [Fact]
    public void Valid_manifest_with_owner_object_parses_two_plugins()
    {
        var json = """
            {
                "name": "my-marketplace",
                "owner": { "name": "alice" },
                "plugins": [
                    { "name": "plugin-one", "source": "owner/plugin-one" },
                    { "name": "plugin-two", "source": "owner/plugin-two", "description": "A plugin", "version": "1.0.0", "category": "tools", "tags": ["a","b"] }
                ]
            }
            """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal("my-marketplace", manifest.Name);
        Assert.Equal("alice", manifest.OwnerName);
        Assert.Equal(2, manifest.Plugins.Count);
        Assert.Equal("plugin-one", manifest.Plugins[0].Name);
        Assert.Equal("owner/plugin-one", manifest.Plugins[0].Source);
        Assert.Equal("plugin-two", manifest.Plugins[1].Name);
        Assert.Equal("owner/plugin-two", manifest.Plugins[1].Source);
        Assert.Equal("A plugin", manifest.Plugins[1].Description);
        Assert.Equal("1.0.0", manifest.Plugins[1].Version);
        Assert.Equal("tools", manifest.Plugins[1].Category);
        Assert.Equal(["a", "b"], manifest.Plugins[1].Tags);
    }

    [Fact]
    public void Owner_as_string_is_parsed()
    {
        var json = """
            {
                "name": "test-market",
                "owner": "alice",
                "plugins": []
            }
            """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal("alice", manifest.OwnerName);
    }

    [Fact]
    public void Missing_name_is_error()
    {
        var json = """
            {
                "plugins": []
            }
            """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(manifest);
        Assert.NotNull(error);
        Assert.Contains("name", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_plugins_is_error()
    {
        var json = """
            {
                "name": "my-marketplace"
            }
            """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(manifest);
        Assert.NotNull(error);
        Assert.Contains("plugins", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_entry_without_source_is_skipped_others_kept()
    {
        var json = """
            {
                "name": "my-marketplace",
                "plugins": [
                    { "name": "good-plugin", "source": "owner/good-plugin" },
                    { "name": "bad-plugin" }
                ]
            }
            """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Single(manifest.Plugins);
        Assert.Equal("good-plugin", manifest.Plugins[0].Name);
    }

    [Fact]
    public void Entry_with_spaces_in_name_is_skipped()
    {
        var json = """
            {
                "name": "my-marketplace",
                "plugins": [
                    { "name": "has spaces", "source": "owner/plugin" },
                    { "name": "valid-plugin", "source": "owner/valid-plugin" }
                ]
            }
            """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Single(manifest.Plugins);
        Assert.Equal("valid-plugin", manifest.Plugins[0].Name);
    }

    [Fact]
    public void PluginRoot_from_metadata_is_parsed()
    {
        var json = """
            {
                "name": "my-marketplace",
                "plugins": [],
                "metadata": {
                    "pluginRoot": "./plugins"
                }
            }
            """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal("./plugins", manifest.PluginRoot);
    }

    [Fact]
    public void Entry_source_as_git_object_is_normalized_to_url()
    {
        var json = """
            {
                "name": "my-marketplace",
                "plugins": [
                    { "name": "my-plugin", "source": { "source": "git", "url": "https://x/y.git" } }
                ]
            }
            """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Single(manifest.Plugins);
        Assert.Equal("https://x/y.git", manifest.Plugins[0].Source);
    }

    [Fact]
    public void Entry_source_as_github_object_is_normalized_to_repo()
    {
        var json = """
            {
                "name": "my-marketplace",
                "plugins": [
                    { "name": "my-plugin", "source": { "source": "github", "repo": "o/r" } }
                ]
            }
            """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Single(manifest.Plugins);
        Assert.Equal("o/r", manifest.Plugins[0].Source);
    }

    [Fact]
    public void Tags_default_to_empty_when_absent()
    {
        var json = """
            {
                "name": "my-marketplace",
                "plugins": [
                    { "name": "my-plugin", "source": "owner/my-plugin" }
                ]
            }
            """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Single(manifest.Plugins);
        Assert.Empty(manifest.Plugins[0].Tags);
    }

    [Fact]
    public void Corrupt_json_is_error()
    {
        var json = """{ "name": "broken" """;

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(manifest);
        Assert.NotNull(error);
        Assert.Contains("Invalid marketplace.json", error, StringComparison.OrdinalIgnoreCase);
    }

    // ── ROBUSTNESS #3: non-string optional fields do not throw ───────────────

    [Fact]
    public void Manifest_with_non_string_optional_fields_does_not_throw()
    {
        // owner is numeric (42), one plugin has version:false — both are non-string.
        // Parser must not throw; owner → null; that plugin is still present with Version null.
        var json = """
            {
                "name": "tolerant-market",
                "owner": 42,
                "plugins": [
                    { "name": "my-plugin", "source": "owner/my-plugin", "version": false, "description": 99, "category": true }
                ]
            }
            """;

        var exception = Record.Exception(() => MarketplaceManifestParser.Parse(json));
        Assert.Null(exception);

        var (manifest, error) = MarketplaceManifestParser.Parse(json);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Null(manifest.OwnerName);
        Assert.Single(manifest.Plugins);
        Assert.Equal("my-plugin", manifest.Plugins[0].Name);
        Assert.Null(manifest.Plugins[0].Version);
    }
}
