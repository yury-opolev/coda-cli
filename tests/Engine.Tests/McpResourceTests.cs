using System.Text.Json;
using Coda.Agent;
using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// Tests for MCP resources/list + resources/read via McpStdioClient, and the
/// ListMcpResourcesTool / ReadMcpResourceTool ITool wrappers.
///
/// NOTE — Elicitation (server-initiated requests) is out of scope for Coda's MCP
/// implementation and is not tested here.
/// </summary>
public sealed class McpResourceInfoTests
{
    [Fact]
    public void Record_stores_all_fields()
    {
        var info = new McpResourceInfo("my-server", "file:///foo.txt", "Foo", "text/plain");

        Assert.Equal("my-server", info.ServerName);
        Assert.Equal("file:///foo.txt", info.Uri);
        Assert.Equal("Foo", info.Name);
        Assert.Equal("text/plain", info.MimeType);
    }

    [Fact]
    public void Record_allows_null_mime_type()
    {
        var info = new McpResourceInfo("s", "uri://x", "X", null);

        Assert.Null(info.MimeType);
    }
}

/// <summary>Tests for McpStdioClient resource methods using the scripted RPC harness.</summary>
public sealed class McpStdioClientResourceTests
{
    // Helpers -----------------------------------------------------------------

    private static (ScriptedMcpStdioClient Client, Action<string> Dispatch) BuildScriptedClient(string serverName = "test-server")
    {
        var writer = new StringWriter();
        var rpc = new McpRpcConnection(writer);
        var client = new ScriptedMcpStdioClient(serverName, rpc);
        return (client, rpc.DispatchLine);
    }

    // ListResourcesAsync tests ------------------------------------------------

    [Fact]
    public async Task ListResources_parses_resources_array()
    {
        var (client, dispatch) = BuildScriptedClient("srv");

        var task = client.ListResourcesAsync();

        dispatch(
            """{"jsonrpc":"2.0","id":1,"result":{"resources":[{"uri":"file:///a.txt","name":"A","mimeType":"text/plain"},{"uri":"file:///b.png","name":"B"}]}}""");

        var resources = await task;

        Assert.Equal(2, resources.Count);
        Assert.Equal("srv", resources[0].ServerName);
        Assert.Equal("file:///a.txt", resources[0].Uri);
        Assert.Equal("A", resources[0].Name);
        Assert.Equal("text/plain", resources[0].MimeType);
        Assert.Equal("file:///b.png", resources[1].Uri);
        Assert.Null(resources[1].MimeType);
    }

    [Fact]
    public async Task ListResources_returns_empty_when_server_errors()
    {
        var (client, dispatch) = BuildScriptedClient();

        var task = client.ListResourcesAsync();

        dispatch("""{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Method not found"}}""");

        var resources = await task;

        Assert.Empty(resources);
    }

    [Fact]
    public async Task ListResources_returns_empty_when_result_has_no_resources_array()
    {
        var (client, dispatch) = BuildScriptedClient();

        var task = client.ListResourcesAsync();

        dispatch("""{"jsonrpc":"2.0","id":1,"result":{}}""");

        var resources = await task;

        Assert.Empty(resources);
    }

    // ReadResourceAsync tests -------------------------------------------------

    [Fact]
    public async Task ReadResource_concatenates_text_contents()
    {
        var (client, dispatch) = BuildScriptedClient();

        var task = client.ReadResourceAsync("file:///foo.txt");

        dispatch(
            """{"jsonrpc":"2.0","id":1,"result":{"contents":[{"uri":"file:///foo.txt","mimeType":"text/plain","text":"Hello, "},{"uri":"file:///foo.txt","mimeType":"text/plain","text":"world!"}]}}""");

        var text = await task;

        Assert.Equal("Hello, world!", text);
    }

    [Fact]
    public async Task ReadResource_returns_placeholder_for_blob_content()
    {
        var (client, dispatch) = BuildScriptedClient();

        var task = client.ReadResourceAsync("file:///image.png");

        dispatch(
            """{"jsonrpc":"2.0","id":1,"result":{"contents":[{"uri":"file:///image.png","mimeType":"image/png","blob":"iVBORw=="}]}}""");

        var text = await task;

        Assert.Equal("[binary content]", text);
    }

    [Fact]
    public async Task ReadResource_mixes_text_and_blob_contents()
    {
        var (client, dispatch) = BuildScriptedClient();

        var task = client.ReadResourceAsync("uri://mixed");

        dispatch(
            """{"jsonrpc":"2.0","id":1,"result":{"contents":[{"uri":"uri://mixed","text":"header "},{"uri":"uri://mixed","blob":"xyz=="},{"uri":"uri://mixed","text":"footer"}]}}""");

        var text = await task;

        Assert.Equal("header [binary content]footer", text);
    }

    [Fact]
    public async Task ReadResource_returns_empty_string_when_no_contents()
    {
        var (client, dispatch) = BuildScriptedClient();

        var task = client.ReadResourceAsync("uri://empty");

        dispatch("""{"jsonrpc":"2.0","id":1,"result":{"contents":[]}}""");

        var text = await task;

        Assert.Equal(string.Empty, text);
    }
}

/// <summary>Tests for McpClientManager resource fan-out.</summary>
public sealed class McpClientManagerResourceTests
{
    [Fact]
    public async Task ListResources_aggregates_from_all_clients()
    {
        var rpc1 = new McpRpcConnection(new StringWriter());
        var rpc2 = new McpRpcConnection(new StringWriter());
        var client1 = new ScriptedMcpStdioClient("server1", rpc1);
        var client2 = new ScriptedMcpStdioClient("server2", rpc2);

        var manager = new McpClientManager([client1, client2]);

        var task = manager.ListResourcesAsync();

        rpc1.DispatchLine("""{"jsonrpc":"2.0","id":1,"result":{"resources":[{"uri":"uri://a","name":"A"}]}}""");
        rpc2.DispatchLine("""{"jsonrpc":"2.0","id":1,"result":{"resources":[{"uri":"uri://b","name":"B"}]}}""");

        var resources = await task;

        Assert.Equal(2, resources.Count);
        Assert.Contains(resources, r => r.Uri == "uri://a" && r.ServerName == "server1");
        Assert.Contains(resources, r => r.Uri == "uri://b" && r.ServerName == "server2");
    }

    [Fact]
    public async Task ListResources_swallows_per_client_errors()
    {
        var rpc1 = new McpRpcConnection(new StringWriter());
        var rpc2 = new McpRpcConnection(new StringWriter());
        var client1 = new ScriptedMcpStdioClient("ok-server", rpc1);
        var client2 = new ScriptedMcpStdioClient("bad-server", rpc2);

        var manager = new McpClientManager([client1, client2]);

        var task = manager.ListResourcesAsync();

        rpc1.DispatchLine("""{"jsonrpc":"2.0","id":1,"result":{"resources":[{"uri":"uri://ok","name":"OK"}]}}""");
        rpc2.DispatchLine("""{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Not supported"}}""");

        var resources = await task;

        Assert.Single(resources);
        Assert.Equal("uri://ok", resources[0].Uri);
    }

    [Fact]
    public async Task ReadResource_finds_client_by_server_name()
    {
        var rpc = new McpRpcConnection(new StringWriter());
        var client = new ScriptedMcpStdioClient("my-server", rpc);
        var manager = new McpClientManager([client]);

        var task = manager.ReadResourceAsync("my-server", "uri://doc");

        rpc.DispatchLine("""{"jsonrpc":"2.0","id":1,"result":{"contents":[{"uri":"uri://doc","text":"doc content"}]}}""");

        var text = await task;

        Assert.Equal("doc content", text);
    }

    [Fact]
    public async Task ReadResource_returns_error_message_for_unknown_server()
    {
        var manager = new McpClientManager([]);

        var text = await manager.ReadResourceAsync("no-such-server", "uri://x");

        Assert.Contains("no-such-server", text);
    }
}

/// <summary>Tests for the ListMcpResourcesTool ITool wrapper.</summary>
public sealed class ListMcpResourcesToolTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolContext FakeContext() => new(WorkingDirectory: ".");

    [Fact]
    public async Task Tool_lists_resources_formatted()
    {
        var rpc = new McpRpcConnection(new StringWriter());
        var client = new ScriptedMcpStdioClient("mysvr", rpc);
        var manager = new McpClientManager([client]);
        var tool = new ListMcpResourcesTool(manager);

        var task = tool.ExecuteAsync(Json("{}"), FakeContext());

        rpc.DispatchLine(
            """{"jsonrpc":"2.0","id":1,"result":{"resources":[{"uri":"file:///readme.md","name":"Readme","mimeType":"text/markdown"}]}}""");

        var result = await task;

        Assert.False(result.IsError);
        Assert.Contains("mysvr", result.Content);
        Assert.Contains("file:///readme.md", result.Content);
        Assert.Contains("Readme", result.Content);
    }

    [Fact]
    public async Task Tool_returns_no_resources_message_when_empty()
    {
        var manager = new McpClientManager([]);
        var tool = new ListMcpResourcesTool(manager);

        var result = await tool.ExecuteAsync(Json("{}"), FakeContext());

        Assert.False(result.IsError);
        Assert.Equal("No MCP resources available.", result.Content);
    }

    [Fact]
    public async Task Tool_filters_by_server_name_when_provided()
    {
        var rpc1 = new McpRpcConnection(new StringWriter());
        var rpc2 = new McpRpcConnection(new StringWriter());
        var client1 = new ScriptedMcpStdioClient("server-a", rpc1);
        var client2 = new ScriptedMcpStdioClient("server-b", rpc2);
        var manager = new McpClientManager([client1, client2]);
        var tool = new ListMcpResourcesTool(manager);

        var task = tool.ExecuteAsync(Json("""{"server":"server-a"}"""), FakeContext());

        // Both clients are queried (fan-out), then results are filtered in memory.
        rpc1.DispatchLine("""{"jsonrpc":"2.0","id":1,"result":{"resources":[{"uri":"uri://only-a","name":"OnlyA"}]}}""");
        rpc2.DispatchLine("""{"jsonrpc":"2.0","id":1,"result":{"resources":[{"uri":"uri://only-b","name":"OnlyB"}]}}""");

        var result = await task;

        Assert.False(result.IsError);
        Assert.Contains("server-a", result.Content);
        Assert.Contains("uri://only-a", result.Content);
        Assert.DoesNotContain("uri://only-b", result.Content);
    }

    [Fact]
    public void Tool_has_expected_metadata()
    {
        var tool = new ListMcpResourcesTool(new McpClientManager([]));

        Assert.Equal("list_mcp_resources", tool.Name);
        Assert.True(tool.IsReadOnly);
    }
}

/// <summary>Tests for the ReadMcpResourceTool ITool wrapper.</summary>
public sealed class ReadMcpResourceToolTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolContext FakeContext() => new(WorkingDirectory: ".");

    [Fact]
    public async Task Tool_reads_resource_content()
    {
        var rpc = new McpRpcConnection(new StringWriter());
        var client = new ScriptedMcpStdioClient("srv", rpc);
        var manager = new McpClientManager([client]);
        var tool = new ReadMcpResourceTool(manager);

        var task = tool.ExecuteAsync(Json("""{"server":"srv","uri":"file:///data.txt"}"""), FakeContext());

        rpc.DispatchLine("""{"jsonrpc":"2.0","id":1,"result":{"contents":[{"uri":"file:///data.txt","text":"the data"}]}}""");

        var result = await task;

        Assert.False(result.IsError);
        Assert.Equal("the data", result.Content);
    }

    [Fact]
    public async Task Tool_returns_error_when_server_missing()
    {
        var manager = new McpClientManager([]);
        var tool = new ReadMcpResourceTool(manager);

        var result = await tool.ExecuteAsync(Json("""{"server":"ghost","uri":"uri://x"}"""), FakeContext());

        Assert.False(result.IsError);
        Assert.Contains("ghost", result.Content);
    }

    [Fact]
    public async Task Tool_returns_error_when_server_arg_missing()
    {
        var manager = new McpClientManager([]);
        var tool = new ReadMcpResourceTool(manager);

        var result = await tool.ExecuteAsync(Json("""{"uri":"uri://x"}"""), FakeContext());

        Assert.True(result.IsError);
        Assert.Contains("server", result.Content);
    }

    [Fact]
    public async Task Tool_returns_error_when_uri_arg_missing()
    {
        var manager = new McpClientManager([]);
        var tool = new ReadMcpResourceTool(manager);

        var result = await tool.ExecuteAsync(Json("""{"server":"srv"}"""), FakeContext());

        Assert.True(result.IsError);
        Assert.Contains("uri", result.Content);
    }

    [Fact]
    public void Tool_has_expected_metadata()
    {
        var tool = new ReadMcpResourceTool(new McpClientManager([]));

        Assert.Equal("read_mcp_resource", tool.Name);
        Assert.True(tool.IsReadOnly);
    }
}

/// <summary>
/// Test-only subclass of McpStdioClient that skips process launching and accepts
/// an already-constructed McpRpcConnection. Mirrors the pattern used in McpTests.cs
/// (which tests McpRpcConnection directly).
/// </summary>
public sealed class ScriptedMcpStdioClient : McpStdioClient
{
    public ScriptedMcpStdioClient(string serverName, McpRpcConnection rpc)
        : base(serverName, rpc)
    {
    }
}
