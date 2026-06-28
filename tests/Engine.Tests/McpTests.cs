using System.Text.Json;
using Coda.Mcp;

namespace Engine.Tests;

public sealed class McpRpcConnectionTests
{
    [Fact]
    public async Task Request_is_correlated_to_response_by_id()
    {
        var writer = new StringWriter();
        var conn = new McpRpcConnection(writer);

        var task = conn.SendRequestAsync("ping");
        Assert.Contains("\"method\":\"ping\"", writer.ToString());

        conn.DispatchLine("""{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""");

        var result = await task;
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Error_response_throws_mcp_exception()
    {
        var conn = new McpRpcConnection(new StringWriter());
        var task = conn.SendRequestAsync("boom");

        conn.DispatchLine("""{"jsonrpc":"2.0","id":1,"error":{"code":-1,"message":"bad thing"}}""");

        var ex = await Assert.ThrowsAsync<McpException>(async () => await task);
        Assert.Contains("bad thing", ex.Message);
    }

    [Fact]
    public void Server_notifications_without_our_id_are_ignored()
    {
        var conn = new McpRpcConnection(new StringWriter());
        // Should not throw.
        conn.DispatchLine("""{"jsonrpc":"2.0","method":"notifications/message","params":{}}""");
        conn.DispatchLine("not json");
    }
}

public sealed class McpConfigTests
{
    [Fact]
    public void Parses_stdio_servers()
    {
        const string json = """
            {"mcpServers":{
              "fs":{"command":"npx","args":["-y","server-filesystem"],"env":{"ROOT":"/tmp"}}
            }}
            """;

        var config = McpConfig.Parse(json);

        var fs = Assert.IsType<McpStdioServerConfig>(config["fs"]);
        Assert.Equal("npx", fs.Command);
        Assert.Equal(["-y", "server-filesystem"], fs.Args);
        Assert.Equal("/tmp", fs.Env["ROOT"]);
    }

    [Fact]
    public void Parses_http_server_with_default_oauth_and_headers()
    {
        const string json = """
            {"mcpServers":{
              "remote":{"type":"http","url":"https://mcp.example.com/mcp","headers":{"X-Tenant":"acme"}}
            }}
            """;

        var http = Assert.IsType<McpHttpServerConfig>(McpConfig.Parse(json)["remote"]);
        Assert.Equal("https://mcp.example.com/mcp", http.Url.ToString());
        Assert.Equal("acme", http.Headers["X-Tenant"]);
        Assert.Equal(McpAuthMode.OAuth, http.Auth.Mode);
    }

    [Fact]
    public void Parses_http_auth_block()
    {
        const string json = """
            {"mcpServers":{
              "remote":{"type":"streamable-http","url":"https://mcp.example.com",
                "auth":{"mode":"bearer","token":"secret","clientId":"cid","scopes":["files:read"]}}
            }}
            """;

        var http = Assert.IsType<McpHttpServerConfig>(McpConfig.Parse(json)["remote"]);
        Assert.Equal(McpAuthMode.Bearer, http.Auth.Mode);
        Assert.Equal("secret", http.Auth.BearerToken);
        Assert.Equal("cid", http.Auth.ClientId);
        Assert.Equal(["files:read"], http.Auth.Scopes);
    }

    [Fact]
    public void Skips_unknown_transport_and_invalid_http_url()
    {
        const string json = """
            {"mcpServers":{
              "legacy":{"type":"sse","url":"https://example.com"},
              "bad":{"type":"http"}
            }}
            """;

        var config = McpConfig.Parse(json);
        Assert.False(config.ContainsKey("legacy"));
        Assert.False(config.ContainsKey("bad"));
    }

    [Fact]
    public void Empty_or_invalid_json_yields_no_servers()
    {
        Assert.Empty(McpConfig.Parse("not json"));
        Assert.Empty(McpConfig.Parse("{}"));
    }

    [Fact]
    public void Load_merges_user_then_project_overriding_by_name()
    {
        var userDir = Path.Combine(Path.GetTempPath(), "coda-mcp-user-" + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(Path.GetTempPath(), "coda-mcp-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);
        Directory.CreateDirectory(projectDir);
        try
        {
            File.WriteAllText(Path.Combine(userDir, ".mcp.json"), """
                {"mcpServers":{
                  "shared":{"command":"user-cmd"},
                  "user-only":{"command":"u"}
                }}
                """);
            File.WriteAllText(Path.Combine(projectDir, ".mcp.json"), """
                {"mcpServers":{
                  "shared":{"command":"project-cmd"},
                  "project-only":{"command":"p"}
                }}
                """);

            var config = McpConfig.Load(projectDir, userDir);

            Assert.Equal(3, config.Count);
            Assert.Equal("project-cmd", Assert.IsType<McpStdioServerConfig>(config["shared"]).Command);
            Assert.True(config.ContainsKey("user-only"));
            Assert.True(config.ContainsKey("project-only"));
        }
        finally
        {
            Directory.Delete(userDir, recursive: true);
            Directory.Delete(projectDir, recursive: true);
        }
    }
}

public sealed class McpToolInfoTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ParseList_reads_name_schema_and_readonly_hint()
    {
        var tools = McpToolInfo.ParseList(Json("""
            {"tools":[
              {"name":"read","description":"reads","inputSchema":{"type":"object"},"annotations":{"readOnlyHint":true}},
              {"name":"write","description":"writes"}
            ]}
            """));

        Assert.Equal(2, tools.Count);
        Assert.Equal("read", tools[0].Name);
        Assert.True(tools[0].ReadOnly);
        Assert.False(tools[1].ReadOnly);
    }

    [Fact]
    public void FormatCallResult_concatenates_text_and_reads_error_flag()
    {
        var (text, isError) = McpToolInfo.FormatCallResult(Json("""{"content":[{"type":"text","text":"hello"}]}"""));
        Assert.Equal("hello", text);
        Assert.False(isError);

        var (_, err) = McpToolInfo.FormatCallResult(Json("""{"isError":true,"content":[{"type":"text","text":"boom"}]}"""));
        Assert.True(err);
    }
}
