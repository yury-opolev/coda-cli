using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// An MCP tool call is otherwise unbounded, so once the orchestrator stops killing coda
/// during tool execution (it now sees the tool-progress heartbeat) a hung MCP server would
/// hang the session forever. McpTool bounds the call at the operation layer: a timeout
/// returns a clean error to the model (session keeps running); a caller/turn cancel still
/// propagates so an interrupt unwinds the turn.
/// </summary>
public sealed class McpToolTimeoutTests
{
    private sealed class FakeMcpClient(Func<CancellationToken, Task<(string, bool)>> onCall) : IMcpClient
    {
        public string ServerName => "srv";

        public Task<(string Text, bool IsError)> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
            onCall(cancellationToken);

        public Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> ReadResourceAsync(string uri, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetPromptAsync(string name, JsonNode? arguments, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static McpTool MakeTool(IMcpClient client) =>
        new(client, "srv", new McpToolInfo("do", "does a thing", """{"type":"object"}""", ReadOnly: false));

    private static JsonElement EmptyArgs() => JsonDocument.Parse("{}").RootElement;

    [Fact]
    public void Resolve_default_env_override_and_infinite()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), McpTool.DefaultTimeout);
        Assert.Equal(McpTool.DefaultTimeout, McpTool.ResolveTimeout(null));
        Assert.Equal(McpTool.DefaultTimeout, McpTool.ResolveTimeout("not-a-number"));
        Assert.Equal(TimeSpan.FromSeconds(30), McpTool.ResolveTimeout("30"));
        Assert.Equal(Timeout.InfiniteTimeSpan, McpTool.ResolveTimeout("0"));
        Assert.Equal(Timeout.InfiniteTimeSpan, McpTool.ResolveTimeout("-1"));
    }

    [Fact]
    public async Task Hung_call_times_out_with_a_clean_error_not_a_throw()
    {
        var prev = Environment.GetEnvironmentVariable(McpTool.TimeoutEnv);
        Environment.SetEnvironmentVariable(McpTool.TimeoutEnv, "1");
        try
        {
            var tool = MakeTool(new FakeMcpClient(async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return ("unreached", false);
            }));

            var result = await tool.ExecuteAsync(EmptyArgs(), new ToolContext("."), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains("timed out", result.Content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpTool.TimeoutEnv, prev);
        }
    }

    [Fact]
    public async Task Caller_cancel_propagates_as_cancellation_not_a_timeout_result()
    {
        var tool = MakeTool(new FakeMcpClient(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ("unreached", false);
        }));

        using var cts = new CancellationTokenSource();
        var run = tool.ExecuteAsync(EmptyArgs(), new ToolContext("."), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Normal_result_passes_through_unchanged()
    {
        var tool = MakeTool(new FakeMcpClient(_ => Task.FromResult(("hello", false))));

        var result = await tool.ExecuteAsync(EmptyArgs(), new ToolContext("."), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("hello", result.Content);
    }
}
