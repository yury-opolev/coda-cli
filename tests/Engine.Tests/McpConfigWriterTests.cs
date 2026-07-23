using System.Text.Json.Nodes;
using Coda.Mcp;

namespace Engine.Tests;

public sealed class McpConfigWriterTests
{
    [Fact]
    public void Upsert_creates_file_and_roundtrips_stdio_via_Parse()
    {
        using var dir = new TempDir();

        McpConfigWriter.Upsert(
            McpConfigScope.Project, "github",
            new McpStdioServerConfig("npx", ["-y", "@mcp/github"], new Dictionary<string, string> { ["TOKEN"] = "x" }),
            disabled: false, dir.Path);

        var parsed = McpConfig.Parse(File.ReadAllText(Path.Combine(dir.Path, ".mcp.json")));
        var stdio = Assert.IsType<McpStdioServerConfig>(parsed["github"]);
        Assert.Equal("npx", stdio.Command);
        Assert.Equal(["-y", "@mcp/github"], stdio.Args);
        Assert.Equal("x", stdio.Env["TOKEN"]);
    }

    [Fact]
    public void Upsert_roundtrips_http_with_bearer_auth_via_Parse()
    {
        using var dir = new TempDir();

        McpConfigWriter.Upsert(
            McpConfigScope.Project, "remote",
            new McpHttpServerConfig(new Uri("https://mcp.example.com/mcp"), new Dictionary<string, string>(),
                new McpAuthConfig(McpAuthMode.Bearer, BearerToken: "secret")),
            disabled: false, dir.Path);

        var parsed = McpConfig.Parse(File.ReadAllText(Path.Combine(dir.Path, ".mcp.json")));
        var http = Assert.IsType<McpHttpServerConfig>(parsed["remote"]);
        Assert.Equal("https://mcp.example.com/mcp", http.Url.ToString());
        Assert.Equal(McpAuthMode.Bearer, http.Auth.Mode);
        Assert.Equal("secret", http.Auth.BearerToken);
    }

    [Fact]
    public void Upsert_roundtrips_http_oauth_with_clientid_and_scopes()
    {
        using var dir = new TempDir();

        McpConfigWriter.Upsert(
            McpConfigScope.Project, "remote",
            new McpHttpServerConfig(new Uri("https://x/mcp"), new Dictionary<string, string>(),
                new McpAuthConfig(McpAuthMode.OAuth, ClientId: "cid", Scopes: ["files:read", "files:write"])),
            disabled: false, dir.Path);

        var http = Assert.IsType<McpHttpServerConfig>(
            McpConfig.Parse(File.ReadAllText(Path.Combine(dir.Path, ".mcp.json")))["remote"]);
        Assert.Equal(McpAuthMode.OAuth, http.Auth.Mode);
        Assert.Equal("cid", http.Auth.ClientId);
        Assert.Equal(["files:read", "files:write"], http.Auth.Scopes);
    }

    [Fact]
    public void Upsert_on_corrupt_file_throws_and_preserves_it()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, ".mcp.json");
        const string corrupt = "{ this is not valid json";
        File.WriteAllText(path, corrupt);

        Assert.Throws<McpException>(() => McpConfigWriter.Upsert(
            McpConfigScope.Project, "x",
            new McpStdioServerConfig("c", [], new Dictionary<string, string>()), disabled: false, dir.Path));

        Assert.Equal(corrupt, File.ReadAllText(path)); // never wiped
    }

    [Fact]
    public void Upsert_preserves_other_servers_and_unrelated_keys()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, ".mcp.json");
        File.WriteAllText(path, """{ "keepMe": 1, "mcpServers": { "existing": { "command": "old" } } }""");

        McpConfigWriter.Upsert(McpConfigScope.Project, "added",
            new McpStdioServerConfig("new", [], new Dictionary<string, string>()), disabled: false, dir.Path);

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(1, (int)root["keepMe"]!);
        var servers = (JsonObject)root["mcpServers"]!;
        Assert.True(servers.ContainsKey("existing"));
        Assert.True(servers.ContainsKey("added"));
    }

    [Fact]
    public void Upsert_replaces_an_existing_entry()
    {
        using var dir = new TempDir();

        McpConfigWriter.Upsert(McpConfigScope.Project, "s",
            new McpStdioServerConfig("first", [], new Dictionary<string, string>()), disabled: false, dir.Path);
        McpConfigWriter.Upsert(McpConfigScope.Project, "s",
            new McpStdioServerConfig("second", [], new Dictionary<string, string>()), disabled: false, dir.Path);

        var parsed = McpConfig.Parse(File.ReadAllText(Path.Combine(dir.Path, ".mcp.json")));
        Assert.Equal("second", ((McpStdioServerConfig)parsed["s"]).Command);
    }

    [Fact]
    public void Remove_deletes_only_the_target_and_reports_presence()
    {
        using var dir = new TempDir();
        McpConfigWriter.Upsert(McpConfigScope.Project, "a", new McpStdioServerConfig("a", [], new Dictionary<string, string>()), false, dir.Path);
        McpConfigWriter.Upsert(McpConfigScope.Project, "b", new McpStdioServerConfig("b", [], new Dictionary<string, string>()), false, dir.Path);

        Assert.True(McpConfigWriter.Remove(McpConfigScope.Project, "a", dir.Path));
        Assert.False(McpConfigWriter.Remove(McpConfigScope.Project, "missing", dir.Path));

        var parsed = McpConfig.Parse(File.ReadAllText(Path.Combine(dir.Path, ".mcp.json")));
        Assert.False(parsed.ContainsKey("a"));
        Assert.True(parsed.ContainsKey("b"));
    }

    [Fact]
    public void Remove_on_missing_file_returns_false()
    {
        using var dir = new TempDir();
        Assert.False(McpConfigWriter.Remove(McpConfigScope.Project, "x", dir.Path));
    }

    [Fact]
    public void Upsert_disabled_and_SetDisabled_toggle_the_flag()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, ".mcp.json");

        McpConfigWriter.Upsert(McpConfigScope.Project, "s",
            new McpStdioServerConfig("cmd", [], new Dictionary<string, string>()), disabled: true, dir.Path);
        Assert.True(Disabled(path, "s"));

        Assert.True(McpConfigWriter.SetDisabled(McpConfigScope.Project, "s", disabled: false, dir.Path));
        Assert.Null(((JsonObject)((JsonObject)JsonNode.Parse(File.ReadAllText(path))!)["mcpServers"]!)["s"]!.AsObject()["disabled"]);

        Assert.True(McpConfigWriter.SetDisabled(McpConfigScope.Project, "s", disabled: true, dir.Path));
        Assert.True(Disabled(path, "s"));

        Assert.False(McpConfigWriter.SetDisabled(McpConfigScope.Project, "missing", true, dir.Path));
    }

    [Fact]
    public void ReplaceEntry_renames_and_preserves_unknown_entry_and_root_properties()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, ".mcp.json");
        File.WriteAllText(path, """
            {
              "vendorRoot": { "keep": true },
              "mcpServers": {
                "old": { "command": "before", "vendorEntry": { "preserve": 1 } }
              }
            }
            """);

        McpConfigWriter.ReplaceEntry(
            McpConfigScope.Project, "old", "new",
            new McpStdioServerConfig("after", ["--new"], new Dictionary<string, string>()),
            disabled: true, dir.Path);

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        var servers = (JsonObject)root["mcpServers"]!;
        Assert.False(servers.ContainsKey("old"));
        var entry = (JsonObject)servers["new"]!;
        Assert.Equal("after", entry["command"]!.GetValue<string>());
        Assert.Equal("--new", entry["args"]![0]!.GetValue<string>());
        Assert.True(entry["disabled"]!.GetValue<bool>());
        Assert.Equal(1, entry["vendorEntry"]!["preserve"]!.GetValue<int>());
        Assert.True(root["vendorRoot"]!["keep"]!.GetValue<bool>());
    }

    [Fact]
    public void ReplaceEntry_switches_transports_without_retaining_known_stale_properties()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, ".mcp.json");
        File.WriteAllText(path, """
            {
              "mcpServers": {
                "stdio": {
                  "type": "stdio",
                  "command": "old",
                  "args": ["old"],
                  "env": { "OLD": "1" },
                  "url": "https://stale.example/mcp",
                  "headers": { "Stale": "yes" },
                  "auth": { "mode": "bearer", "token": "stale" },
                  "disabled": true,
                  "vendor": "keep"
                },
                "http": {
                  "type": "http",
                  "url": "https://old.example/mcp",
                  "headers": { "Old": "yes" },
                  "auth": { "mode": "bearer", "token": "old" },
                  "vendor": "keep"
                }
              }
            }
            """);

        McpConfigWriter.ReplaceEntry(
            McpConfigScope.Project, "stdio", "remote",
            new McpHttpServerConfig(new Uri("https://new.example/mcp"),
                new Dictionary<string, string> { ["New"] = "yes" }, McpAuthConfig.Default),
            disabled: false, dir.Path);
        McpConfigWriter.ReplaceEntry(
            McpConfigScope.Project, "http", "local",
            new McpStdioServerConfig("new", ["--new"], new Dictionary<string, string> { ["NEW"] = "1" }),
            disabled: false, dir.Path);

        var servers = Servers(path);
        var remote = (JsonObject)servers["remote"]!;
        Assert.Equal("http", remote["type"]!.GetValue<string>());
        Assert.Equal("https://new.example/mcp", remote["url"]!.GetValue<string>());
        Assert.Null(remote["command"]);
        Assert.Null(remote["args"]);
        Assert.Null(remote["env"]);
        Assert.Null(remote["auth"]);
        Assert.Null(remote["disabled"]);
        Assert.Equal("keep", remote["vendor"]!.GetValue<string>());

        var local = (JsonObject)servers["local"]!;
        Assert.Equal("new", local["command"]!.GetValue<string>());
        Assert.Null(local["type"]);
        Assert.Null(local["url"]);
        Assert.Null(local["headers"]);
        Assert.Null(local["auth"]);
        Assert.Equal("keep", local["vendor"]!.GetValue<string>());
    }

    [Fact]
    public void ReplaceEntry_allows_same_name_incremental_edit()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, ".mcp.json");
        File.WriteAllText(path, """{ "mcpServers": { "server": { "command": "old", "vendor": true } } }""");

        McpConfigWriter.ReplaceEntry(
            McpConfigScope.Project, "server", "server",
            new McpStdioServerConfig("new", [], new Dictionary<string, string>()),
            disabled: false, dir.Path);

        var entry = (JsonObject)Servers(path)["server"]!;
        Assert.Equal("new", entry["command"]!.GetValue<string>());
        Assert.True(entry["vendor"]!.GetValue<bool>());
    }

    [Fact]
    public void ReplaceEntry_failures_leave_original_bytes_unchanged()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, ".mcp.json");
        const string valid = """{ "mcpServers": { "source": { "command": "old" }, "taken": { "command": "taken" } } }""";
        var config = new McpStdioServerConfig("new", [], new Dictionary<string, string>());

        File.WriteAllText(path, valid);
        Assert.Throws<McpException>(() => McpConfigWriter.ReplaceEntry(
            McpConfigScope.Project, "source", "taken", config, false, dir.Path));
        Assert.Equal(valid, File.ReadAllText(path));

        Assert.Throws<McpException>(() => McpConfigWriter.ReplaceEntry(
            McpConfigScope.Project, "missing", "new", config, false, dir.Path));
        Assert.Equal(valid, File.ReadAllText(path));

        Assert.Throws<ArgumentException>(() => McpConfigWriter.ReplaceEntry(
            McpConfigScope.Project, " ", "new", config, false, dir.Path));
        Assert.Equal(valid, File.ReadAllText(path));
        Assert.Throws<ArgumentException>(() => McpConfigWriter.ReplaceEntry(
            McpConfigScope.Project, "source", " ", config, false, dir.Path));
        Assert.Equal(valid, File.ReadAllText(path));

        const string corrupt = "{ bad";
        File.WriteAllText(path, corrupt);
        Assert.Throws<McpException>(() => McpConfigWriter.ReplaceEntry(
            McpConfigScope.Project, "source", "new", config, false, dir.Path));
        Assert.Equal(corrupt, File.ReadAllText(path));
    }

    [Fact]
    public void ReplaceEntry_on_missing_file_throws_without_creating_it()
    {
        using var dir = new TempDir();

        Assert.Throws<McpException>(() => McpConfigWriter.ReplaceEntry(
            McpConfigScope.Project, "source", "new",
            new McpStdioServerConfig("cmd", [], new Dictionary<string, string>()),
            disabled: false, dir.Path));

        Assert.False(File.Exists(Path.Combine(dir.Path, ".mcp.json")));
    }

    [Fact]
    public void ReplaceEntry_uses_the_requested_user_and_project_paths()
    {
        using var project = new TempDir();
        using var user = new TempDir();
        var config = new McpStdioServerConfig("new", [], new Dictionary<string, string>());
        File.WriteAllText(Path.Combine(project.Path, ".mcp.json"),
            """{ "mcpServers": { "project": { "command": "old-project" } } }""");
        File.WriteAllText(Path.Combine(user.Path, ".mcp.json"),
            """{ "mcpServers": { "user": { "command": "old-user" } } }""");

        McpConfigWriter.ReplaceEntry(McpConfigScope.User, "user", "renamed-user", config, false, project.Path, user.Path);
        McpConfigWriter.ReplaceEntry(McpConfigScope.Project, "project", "renamed-project", config, false, project.Path, user.Path);

        Assert.True(Servers(Path.Combine(user.Path, ".mcp.json")).ContainsKey("renamed-user"));
        Assert.True(Servers(Path.Combine(project.Path, ".mcp.json")).ContainsKey("renamed-project"));
    }

    [Fact]
    public void Writer_operations_leave_no_temp_files_after_sequential_writes()
    {
        using var dir = new TempDir();
        var config = new McpStdioServerConfig("cmd", [], new Dictionary<string, string>());
        var legacyTemp = Path.Combine(dir.Path, ".mcp.json.tmp");
        File.WriteAllText(legacyTemp, "unrelated");

        McpConfigWriter.Upsert(McpConfigScope.Project, "server", config, false, dir.Path);
        McpConfigWriter.SetDisabled(McpConfigScope.Project, "server", true, dir.Path);
        McpConfigWriter.ReplaceEntry(McpConfigScope.Project, "server", "renamed", config, false, dir.Path);
        McpConfigWriter.Remove(McpConfigScope.Project, "renamed", dir.Path);

        Assert.Empty(Directory.EnumerateFiles(dir.Path, ".mcp.json.*.tmp"));
        Assert.Equal("unrelated", File.ReadAllText(legacyTemp));
    }

    private static bool Disabled(string path, string name)
    {
        var servers = Servers(path);
        return ((JsonObject)servers[name]!)["disabled"]?.GetValue<bool>() == true;
    }

    private static JsonObject Servers(string path) =>
        (JsonObject)((JsonObject)JsonNode.Parse(File.ReadAllText(path))!)["mcpServers"]!;

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coda-mcp-w-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(this.Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(this.Path))
                {
                    Directory.Delete(this.Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
