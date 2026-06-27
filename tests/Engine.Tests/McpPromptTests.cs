using System.Text.Json;
using Coda.Agent;
using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// Tests for MCP prompts/list + prompts/get via McpStdioClient, and the
/// ListMcpPromptsTool / GetMcpPromptTool ITool wrappers.
/// </summary>
public sealed class McpPromptInfoTests
{
    [Fact]
    public void Record_stores_all_fields()
    {
        var info = new McpPromptInfo("my-server", "greet", "Greets the user");

        Assert.Equal("my-server", info.ServerName);
        Assert.Equal("greet", info.Name);
        Assert.Equal("Greets the user", info.Description);
    }

    [Fact]
    public void Record_allows_null_description()
    {
        var info = new McpPromptInfo("s", "prompt-name", null);

        Assert.Null(info.Description);
    }
}

/// <summary>Tests for McpStdioClient prompt methods using the scripted RPC harness.</summary>
public sealed class McpStdioClientPromptTests
{
    // Helpers -----------------------------------------------------------------

    private static (ScriptedMcpStdioClient Client, Action<string> Dispatch) BuildScriptedClient(string serverName = "test-server")
    {
        var writer = new StringWriter();
        var rpc = new McpRpcConnection(writer);
        var client = new ScriptedMcpStdioClient(serverName, rpc);
        return (client, rpc.DispatchLine);
    }

    // ListPromptsAsync tests --------------------------------------------------

    [Fact]
    public async Task ListPrompts_parses_prompts_array()
    {
        var (client, dispatch) = BuildScriptedClient("srv");

        var task = client.ListPromptsAsync();

        dispatch(
            """{"jsonrpc":"2.0","id":1,"result":{"prompts":[{"name":"greet","description":"Greets user"},{"name":"summarize"}]}}""");

        var prompts = await task;

        Assert.Equal(2, prompts.Count);
        Assert.Equal("srv", prompts[0].ServerName);
        Assert.Equal("greet", prompts[0].Name);
        Assert.Equal("Greets user", prompts[0].Description);
        Assert.Equal("summarize", prompts[1].Name);
        Assert.Null(prompts[1].Description);
    }

    [Fact]
    public async Task ListPrompts_returns_empty_when_server_errors()
    {
        var (client, dispatch) = BuildScriptedClient();

        var task = client.ListPromptsAsync();

        dispatch("""{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Method not found"}}""");

        var prompts = await task;

        Assert.Empty(prompts);
    }

    [Fact]
    public async Task ListPrompts_returns_empty_when_result_has_no_prompts_array()
    {
        var (client, dispatch) = BuildScriptedClient();

        var task = client.ListPromptsAsync();

        dispatch("""{"jsonrpc":"2.0","id":1,"result":{}}""");

        var prompts = await task;

        Assert.Empty(prompts);
    }

    // GetPromptAsync tests ----------------------------------------------------

    [Fact]
    public async Task GetPrompt_concatenates_messages_as_role_text_pairs()
    {
        var (client, dispatch) = BuildScriptedClient();

        var task = client.GetPromptAsync("greet", null);

        dispatch(
            """{"jsonrpc":"2.0","id":1,"result":{"messages":[{"role":"user","content":{"type":"text","text":"Hello!"}},{"role":"assistant","content":{"type":"text","text":"Hi there!"}}]}}""");

        var text = await task;

        Assert.Equal("user: Hello!\nassistant: Hi there!", text);
    }

    [Fact]
    public async Task GetPrompt_returns_single_message()
    {
        var (client, dispatch) = BuildScriptedClient();

        var task = client.GetPromptAsync("greet", null);

        dispatch(
            """{"jsonrpc":"2.0","id":1,"result":{"messages":[{"role":"user","content":{"type":"text","text":"Say hi"}}]}}""");

        var text = await task;

        Assert.Equal("user: Say hi", text);
    }

    [Fact]
    public async Task GetPrompt_throws_argument_exception_on_empty_name()
    {
        var (client, _) = BuildScriptedClient();

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetPromptAsync("", null));
    }

    [Fact]
    public async Task GetPrompt_propagates_mcp_exception()
    {
        var (client, dispatch) = BuildScriptedClient();

        var task = client.GetPromptAsync("unknown-prompt", null);

        dispatch("""{"jsonrpc":"2.0","id":1,"error":{"code":-32602,"message":"Prompt not found"}}""");

        await Assert.ThrowsAsync<McpException>(() => task);
    }
}

/// <summary>Tests for McpClientManager prompt fan-out.</summary>
public sealed class McpClientManagerPromptTests
{
    [Fact]
    public async Task ListPrompts_aggregates_from_all_clients()
    {
        var rpc1 = new McpRpcConnection(new StringWriter());
        var rpc2 = new McpRpcConnection(new StringWriter());
        var client1 = new ScriptedMcpStdioClient("server1", rpc1);
        var client2 = new ScriptedMcpStdioClient("server2", rpc2);

        var manager = new McpClientManager([client1, client2]);

        var task = manager.ListPromptsAsync();

        rpc1.DispatchLine("""{"jsonrpc":"2.0","id":1,"result":{"prompts":[{"name":"prompt-a"}]}}""");
        rpc2.DispatchLine("""{"jsonrpc":"2.0","id":1,"result":{"prompts":[{"name":"prompt-b"}]}}""");

        var prompts = await task;

        Assert.Equal(2, prompts.Count);
        Assert.Contains(prompts, p => p.Name == "prompt-a" && p.ServerName == "server1");
        Assert.Contains(prompts, p => p.Name == "prompt-b" && p.ServerName == "server2");
    }

    [Fact]
    public async Task ListPrompts_swallows_per_client_errors()
    {
        var rpc1 = new McpRpcConnection(new StringWriter());
        var rpc2 = new McpRpcConnection(new StringWriter());
        var client1 = new ScriptedMcpStdioClient("ok-server", rpc1);
        var client2 = new ScriptedMcpStdioClient("bad-server", rpc2);

        var manager = new McpClientManager([client1, client2]);

        var task = manager.ListPromptsAsync();

        rpc1.DispatchLine("""{"jsonrpc":"2.0","id":1,"result":{"prompts":[{"name":"good-prompt"}]}}""");
        rpc2.DispatchLine("""{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Not supported"}}""");

        var prompts = await task;

        Assert.Single(prompts);
        Assert.Equal("good-prompt", prompts[0].Name);
    }

    [Fact]
    public async Task GetPrompt_finds_client_by_server_name()
    {
        var rpc = new McpRpcConnection(new StringWriter());
        var client = new ScriptedMcpStdioClient("my-server", rpc);
        var manager = new McpClientManager([client]);

        var task = manager.GetPromptAsync("my-server", "greet");

        rpc.DispatchLine(
            """{"jsonrpc":"2.0","id":1,"result":{"messages":[{"role":"user","content":{"type":"text","text":"Hello!"}}]}}""");

        var text = await task;

        Assert.Equal("user: Hello!", text);
    }

    [Fact]
    public async Task GetPrompt_returns_message_for_unknown_server()
    {
        var manager = new McpClientManager([]);

        var text = await manager.GetPromptAsync("no-such-server", "greet");

        Assert.Contains("no-such-server", text);
    }
}

/// <summary>Tests for the ListMcpPromptsTool ITool wrapper.</summary>
public sealed class ListMcpPromptsToolTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolContext FakeContext() => new(WorkingDirectory: ".");

    [Fact]
    public async Task Tool_lists_prompts_formatted()
    {
        var rpc = new McpRpcConnection(new StringWriter());
        var client = new ScriptedMcpStdioClient("mysvr", rpc);
        var manager = new McpClientManager([client]);
        var tool = new ListMcpPromptsTool(manager);

        var task = tool.ExecuteAsync(Json("{}"), FakeContext());

        rpc.DispatchLine(
            """{"jsonrpc":"2.0","id":1,"result":{"prompts":[{"name":"greet","description":"Greets the user"}]}}""");

        var result = await task;

        Assert.False(result.IsError);
        Assert.Contains("mysvr", result.Content);
        Assert.Contains("greet", result.Content);
        Assert.Contains("Greets the user", result.Content);
    }

    [Fact]
    public async Task Tool_returns_no_prompts_message_when_empty()
    {
        var manager = new McpClientManager([]);
        var tool = new ListMcpPromptsTool(manager);

        var result = await tool.ExecuteAsync(Json("{}"), FakeContext());

        Assert.False(result.IsError);
        Assert.Equal("No MCP prompts available.", result.Content);
    }

    [Fact]
    public async Task Tool_filters_by_server_name_when_provided()
    {
        var rpc1 = new McpRpcConnection(new StringWriter());
        var rpc2 = new McpRpcConnection(new StringWriter());
        var client1 = new ScriptedMcpStdioClient("server-a", rpc1);
        var client2 = new ScriptedMcpStdioClient("server-b", rpc2);
        var manager = new McpClientManager([client1, client2]);
        var tool = new ListMcpPromptsTool(manager);

        var task = tool.ExecuteAsync(Json("""{"server":"server-a"}"""), FakeContext());

        rpc1.DispatchLine("""{"jsonrpc":"2.0","id":1,"result":{"prompts":[{"name":"prompt-a"}]}}""");
        rpc2.DispatchLine("""{"jsonrpc":"2.0","id":1,"result":{"prompts":[{"name":"prompt-b"}]}}""");

        var result = await task;

        Assert.False(result.IsError);
        Assert.Contains("server-a", result.Content);
        Assert.Contains("prompt-a", result.Content);
        Assert.DoesNotContain("prompt-b", result.Content);
    }

    [Fact]
    public void Tool_has_expected_metadata()
    {
        var tool = new ListMcpPromptsTool(new McpClientManager([]));

        Assert.Equal("list_mcp_prompts", tool.Name);
        Assert.True(tool.IsReadOnly);
    }
}

/// <summary>Tests for the GetMcpPromptTool ITool wrapper.</summary>
public sealed class GetMcpPromptToolTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolContext FakeContext() => new(WorkingDirectory: ".");

    [Fact]
    public async Task Tool_returns_rendered_prompt_text()
    {
        var rpc = new McpRpcConnection(new StringWriter());
        var client = new ScriptedMcpStdioClient("srv", rpc);
        var manager = new McpClientManager([client]);
        var tool = new GetMcpPromptTool(manager);

        var task = tool.ExecuteAsync(Json("""{"server":"srv","name":"greet"}"""), FakeContext());

        rpc.DispatchLine(
            """{"jsonrpc":"2.0","id":1,"result":{"messages":[{"role":"user","content":{"type":"text","text":"Hello!"}}]}}""");

        var result = await task;

        Assert.False(result.IsError);
        Assert.Equal("user: Hello!", result.Content);
    }

    [Fact]
    public async Task Tool_returns_error_when_server_not_found()
    {
        var manager = new McpClientManager([]);
        var tool = new GetMcpPromptTool(manager);

        var result = await tool.ExecuteAsync(Json("""{"server":"ghost","name":"greet"}"""), FakeContext());

        Assert.False(result.IsError);
        Assert.Contains("ghost", result.Content);
    }

    [Fact]
    public async Task Tool_returns_error_when_server_arg_missing()
    {
        var manager = new McpClientManager([]);
        var tool = new GetMcpPromptTool(manager);

        var result = await tool.ExecuteAsync(Json("""{"name":"greet"}"""), FakeContext());

        Assert.True(result.IsError);
        Assert.Contains("server", result.Content);
    }

    [Fact]
    public async Task Tool_returns_error_when_name_arg_missing()
    {
        var manager = new McpClientManager([]);
        var tool = new GetMcpPromptTool(manager);

        var result = await tool.ExecuteAsync(Json("""{"server":"srv"}"""), FakeContext());

        Assert.True(result.IsError);
        Assert.Contains("name", result.Content);
    }

    [Fact]
    public void Tool_has_expected_metadata()
    {
        var tool = new GetMcpPromptTool(new McpClientManager([]));

        Assert.Equal("get_mcp_prompt", tool.Name);
        Assert.True(tool.IsReadOnly);
    }
}
