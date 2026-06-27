using System.Text.Json.Nodes;
using Coda.Agent.Lsp;

namespace Engine.Tests.Lsp;

/// <summary>
/// TDD tests for Task A1 — LspServerConfigParser extracted from SettingsLoader.
/// </summary>
public sealed class LspServerConfigParserTests
{
    // -------------------------------------------------------------------------
    // Helper — build a JsonObject from a raw JSON literal
    // -------------------------------------------------------------------------

    private static JsonObject Json(string raw)
    {
        return (JsonObject)JsonNode.Parse(raw)!;
    }

    // -------------------------------------------------------------------------
    // ParseEntry — happy path
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseEntry_valid_returns_config()
    {
        var obj = Json("""
            {
              "command": "typescript-language-server",
              "args": ["--stdio", "--log-level", "3"],
              "extensionToLanguage": { ".ts": "typescript", ".tsx": "typescriptreact" },
              "env": { "NODE_ENV": "production" },
              "startupTimeoutMs": 12000
            }
            """);

        var config = LspServerConfigParser.ParseEntry(obj);

        Assert.NotNull(config);
        Assert.Equal("typescript-language-server", config.Command);
        Assert.Equal(["--stdio", "--log-level", "3"], config.Args);
        Assert.Equal(2, config.ExtensionToLanguage.Count);
        Assert.Equal("typescript", config.ExtensionToLanguage[".ts"]);
        Assert.Equal("typescriptreact", config.ExtensionToLanguage[".tsx"]);
        Assert.NotNull(config.Env);
        Assert.Equal("production", config.Env!["NODE_ENV"]);
        Assert.Equal(12000, config.StartupTimeoutMs);
    }

    // -------------------------------------------------------------------------
    // ParseEntry — missing command
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseEntry_missing_command_null()
    {
        var obj = Json("""
            {
              "extensionToLanguage": { ".ts": "typescript" }
            }
            """);

        var config = LspServerConfigParser.ParseEntry(obj);

        Assert.Null(config);
    }

    // -------------------------------------------------------------------------
    // ParseEntry — command with space, non-absolute
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseEntry_command_with_space_non_absolute_null()
    {
        var obj = Json("""
            {
              "command": "node server.js",
              "extensionToLanguage": { ".js": "javascript" }
            }
            """);

        var config = LspServerConfigParser.ParseEntry(obj);

        Assert.Null(config);
    }

    // -------------------------------------------------------------------------
    // ParseEntry — command with space, absolute path (Unix style)
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseEntry_command_with_space_absolute_unix_ok()
    {
        var obj = Json("""
            {
              "command": "/usr/bin/my ls",
              "extensionToLanguage": { ".sh": "shellscript" }
            }
            """);

        var config = LspServerConfigParser.ParseEntry(obj);

        Assert.NotNull(config);
        Assert.Equal("/usr/bin/my ls", config.Command);
    }

    // -------------------------------------------------------------------------
    // ParseEntry — command with space, absolute path (Windows drive)
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseEntry_command_with_space_absolute_windows_ok()
    {
        var obj = Json("""
            {
              "command": "C:\\Program Files\\Node\\node.exe",
              "extensionToLanguage": { ".ts": "typescript" }
            }
            """);

        var config = LspServerConfigParser.ParseEntry(obj);

        Assert.NotNull(config);
        Assert.Equal("C:\\Program Files\\Node\\node.exe", config.Command);
    }

    // -------------------------------------------------------------------------
    // ParseEntry — socket transport rejected
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseEntry_socket_transport_null()
    {
        var obj = Json("""
            {
              "command": "some-lsp",
              "transport": "socket",
              "extensionToLanguage": { ".rs": "rust" }
            }
            """);

        var config = LspServerConfigParser.ParseEntry(obj);

        Assert.Null(config);
    }

    // -------------------------------------------------------------------------
    // ParseEntry — explicit stdio transport accepted
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseEntry_stdio_transport_ok()
    {
        var obj = Json("""
            {
              "command": "some-lsp",
              "transport": "stdio",
              "extensionToLanguage": { ".rs": "rust" }
            }
            """);

        var config = LspServerConfigParser.ParseEntry(obj);

        Assert.NotNull(config);
    }

    // -------------------------------------------------------------------------
    // ParseEntry — absent transport accepted
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseEntry_absent_transport_ok()
    {
        var obj = Json("""
            {
              "command": "gopls",
              "extensionToLanguage": { ".go": "go" }
            }
            """);

        var config = LspServerConfigParser.ParseEntry(obj);

        Assert.NotNull(config);
    }

    // -------------------------------------------------------------------------
    // ParseEntry — no extensionToLanguage key
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseEntry_no_extensionToLanguage_null()
    {
        var obj = Json("""
            {
              "command": "gopls"
            }
            """);

        var config = LspServerConfigParser.ParseEntry(obj);

        Assert.Null(config);
    }

    // -------------------------------------------------------------------------
    // ParseEntry — empty extensionToLanguage object
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseEntry_empty_extensionToLanguage_null()
    {
        var obj = Json("""
            {
              "command": "gopls",
              "extensionToLanguage": {}
            }
            """);

        var config = LspServerConfigParser.ParseEntry(obj);

        Assert.Null(config);
    }

    // -------------------------------------------------------------------------
    // ParseEntry — extension normalization (uppercase + missing dot)
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseEntry_normalizes_extensions_uppercase_and_no_dot()
    {
        var obj = Json("""
            {
              "command": "typescript-language-server",
              "extensionToLanguage": {
                "TS": "typescript",
                "tsx": "typescriptreact"
              }
            }
            """);

        var config = LspServerConfigParser.ParseEntry(obj);

        Assert.NotNull(config);
        Assert.True(config.ExtensionToLanguage.ContainsKey(".ts"), "uppercase TS should become .ts");
        Assert.True(config.ExtensionToLanguage.ContainsKey(".tsx"), "tsx without dot should become .tsx");
        Assert.Equal("typescript", config.ExtensionToLanguage[".ts"]);
        Assert.Equal("typescriptreact", config.ExtensionToLanguage[".tsx"]);
    }

    // -------------------------------------------------------------------------
    // ParseServerMap — skips invalid, keeps valid
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseServerMap_skips_invalid_keeps_valid()
    {
        var serversObject = (JsonObject)JsonNode.Parse("""
            {
              "good": {
                "command": "gopls",
                "extensionToLanguage": { ".go": "go" }
              },
              "bad": {
                "extensionToLanguage": { ".ts": "typescript" }
              }
            }
            """)!;

        var result = LspServerConfigParser.ParseServerMap(serversObject);

        Assert.Single(result);
        Assert.True(result.ContainsKey("good"));
        Assert.False(result.ContainsKey("bad"));
        Assert.Equal("gopls", result["good"].Command);
    }

    // -------------------------------------------------------------------------
    // ParseServerMap — skips non-object nodes
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseServerMap_skips_non_object_node()
    {
        var serversObject = (JsonObject)JsonNode.Parse("""
            {
              "good": {
                "command": "gopls",
                "extensionToLanguage": { ".go": "go" }
              },
              "notAnObject": "just a string"
            }
            """)!;

        var result = LspServerConfigParser.ParseServerMap(serversObject);

        Assert.Single(result);
        Assert.True(result.ContainsKey("good"));
    }

    // -------------------------------------------------------------------------
    // ParseEntry — initializationOptions cloned and present
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseEntry_initializationOptions_cloned_and_present()
    {
        var obj = Json("""
            {
              "command": "typescript-language-server",
              "extensionToLanguage": { ".ts": "typescript" },
              "initializationOptions": { "preferences": { "quotePreference": "auto" } }
            }
            """);

        var config = LspServerConfigParser.ParseEntry(obj);

        Assert.NotNull(config);
        Assert.NotNull(config.InitializationOptions);
    }
}
