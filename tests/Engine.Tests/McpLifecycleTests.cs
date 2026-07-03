using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Mcp;

namespace Engine.Tests;

public sealed class McpLifecycleTests
{
    [Fact]
    public async Task ConnectClientAsync_adds_tools_and_bumps_version()
    {
        var manager = new McpClientManager();
        var client = new FakeMcpClient("github") { Tools = [new McpToolInfo("echo", "d", "{}", true)] };

        var before = manager.Version;
        var result = await manager.ConnectClientAsync(client, default);

        Assert.True(result.Connected);
        Assert.Equal(1, result.ToolCount);
        Assert.True(manager.IsServerConnected("github"));
        Assert.Single(manager.ServerTools("github"));
        Assert.Equal(before + 1, manager.Version);
    }

    [Fact]
    public async Task DisconnectServerAsync_removes_tools_disposes_and_bumps_version()
    {
        var manager = new McpClientManager();
        var client = new FakeMcpClient("github") { Tools = [new McpToolInfo("echo", "d", "{}", true)] };
        await manager.ConnectClientAsync(client, default);
        var versionAfterConnect = manager.Version;

        var removed = await manager.DisconnectServerAsync("github");

        Assert.True(removed);
        Assert.False(manager.IsServerConnected("github"));
        Assert.Empty(manager.ServerTools("github"));
        Assert.True(client.Disposed);
        Assert.Equal(versionAfterConnect + 1, manager.Version);
    }

    [Fact]
    public async Task DisconnectServerAsync_unknown_returns_false()
    {
        var manager = new McpClientManager();
        Assert.False(await manager.DisconnectServerAsync("nope"));
    }

    [Fact]
    public async Task ConnectClientAsync_failure_disposes_and_reports_error()
    {
        var manager = new McpClientManager();
        var client = new FakeMcpClient("bad") { ThrowOnInit = "boom" };

        var result = await manager.ConnectClientAsync(client, default);

        Assert.False(result.Connected);
        Assert.Equal("boom", result.Error);
        Assert.False(manager.IsServerConnected("bad"));
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task ConnectServerAsync_already_connected_fails_without_spawning()
    {
        var manager = new McpClientManager();
        await manager.ConnectClientAsync(new FakeMcpClient("github"), default);

        // IsServerConnected short-circuits before CreateClient, so the stdio config never spawns.
        var result = await manager.ConnectServerAsync(
            "github", new McpStdioServerConfig("x", [], new Dictionary<string, string>()), default);

        Assert.False(result.Connected);
        Assert.Contains("already connected", result.Error!);
    }

    private sealed class FakeMcpClient : IMcpClient
    {
        public FakeMcpClient(string serverName) => this.ServerName = serverName;

        public string ServerName { get; }

        public IReadOnlyList<McpToolInfo> Tools { get; init; } = [];

        public string? ThrowOnInit { get; init; }

        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken ct = default)
            => this.ThrowOnInit is { } msg ? throw new McpException(msg) : Task.FromResult(this.Tools);

        public Task<(string Text, bool IsError)> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
            => Task.FromResult((string.Empty, false));

        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);

        public Task<string> ReadResourceAsync(string uri, CancellationToken ct = default) => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);

        public Task<string> GetPromptAsync(string name, JsonNode? arguments, CancellationToken ct = default) => Task.FromResult(string.Empty);

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
