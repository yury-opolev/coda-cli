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
    public void Parses_stdio_servers_and_skips_non_stdio()
    {
        const string json = """
            {"mcpServers":{
              "fs":{"command":"npx","args":["-y","server-filesystem"],"env":{"ROOT":"/tmp"}},
              "remote":{"type":"http","url":"https://example.com"}
            }}
            """;

        var config = McpConfig.Parse(json);

        Assert.True(config.ContainsKey("fs"));
        Assert.Equal("npx", config["fs"].Command);
        Assert.Equal(["-y", "server-filesystem"], config["fs"].Args);
        Assert.Equal("/tmp", config["fs"].Env["ROOT"]);
        Assert.False(config.ContainsKey("remote"));
    }

    [Fact]
    public void Empty_or_invalid_json_yields_no_servers()
    {
        Assert.Empty(McpConfig.Parse("not json"));
        Assert.Empty(McpConfig.Parse("{}"));
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
