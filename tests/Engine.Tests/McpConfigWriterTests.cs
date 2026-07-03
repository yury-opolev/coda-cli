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

    private static bool Disabled(string path, string name)
    {
        var servers = (JsonObject)((JsonObject)JsonNode.Parse(File.ReadAllText(path))!)["mcpServers"]!;
        return ((JsonObject)servers[name]!)["disabled"]?.GetValue<bool>() == true;
    }

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
