using Coda.Agent;
using Coda.Mcp;

namespace Coda.Tui.Tests;

/// <summary>
/// Tool composition for <c>coda run</c>. Headless deliberately omits the four resource/prompt
/// helper tools that the TUI and <c>serve</c> add, but it DOES get the MCP restart tool: an
/// unattended run is where a hung server costs most and is least likely to be noticed. The
/// composition must also be live, because <c>SessionOptions.ExtraTools</c> is captured once but
/// re-enumerated every turn.
/// </summary>
public sealed class HeadlessRunnerToolCompositionTests
{
    private sealed class StubConfigSource : IMcpServerConfigSource
    {
        public IReadOnlyDictionary<string, McpServerConfig> LoadServers() =>
            new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
    }

    private sealed class StubTool : ITool
    {
        public string Name => "skill";

        public string Description => "d";

        public string InputSchemaJson => "{}";

        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult(string.Empty));
    }

    [Fact]
    public void No_mcp_configured_keeps_only_the_skill_tool()
    {
        var tools = HeadlessRunner.BuildExtraTools(new McpClientManager(), configSource: null, new StubTool());

        Assert.Single(tools);
        Assert.Empty(tools.OfType<RestartMcpServerTool>());
    }

    [Fact]
    public void No_mcp_and_no_skill_tool_is_empty()
    {
        var tools = HeadlessRunner.BuildExtraTools(new McpClientManager(), configSource: null, skillTool: null);

        Assert.Empty(tools);
    }

    [Fact]
    public void Configured_mcp_adds_the_restart_tool_but_not_the_resource_prompt_helpers()
    {
        var tools = HeadlessRunner.BuildExtraTools(new McpClientManager(), new StubConfigSource(), new StubTool());

        Assert.Equal(2, tools.Count);
        Assert.Single(tools.OfType<RestartMcpServerTool>());
        Assert.Empty(tools.OfType<ListMcpResourcesTool>());
        Assert.Empty(tools.OfType<ListMcpPromptsTool>());
    }

    [Fact]
    public async Task Configured_mcp_tracks_the_runtime_instead_of_snapshotting_it()
    {
        var manager = new McpClientManager(new CompositionHttpFactory());
        var tools = HeadlessRunner.BuildExtraTools(manager, new StubConfigSource(), skillTool: null);
        Assert.Single(tools);

        var connected = await manager.ConnectServerAsync(
            "srv",
            new McpHttpServerConfig(
                new Uri("https://example.test/mcp"),
                new Dictionary<string, string>(StringComparer.Ordinal),
                McpAuthConfig.Default));
        Assert.True(connected.Connected);

        Assert.Equal(2, tools.Count);
        Assert.Single(tools.OfType<McpTool>());
    }

    private sealed class CompositionHttpFactory : IMcpHttpClientFactory
    {
        public IMcpClient Create(string serverName, McpHttpServerConfig config) =>
            new CompositionFakeMcpClient(serverName) { Tools = [new McpToolInfo("echo", "d", "{}", true)] };
    }

    private sealed class CompositionFakeMcpClient(string serverName) : IMcpClient
    {
        public string ServerName => serverName;

        public IReadOnlyList<McpToolInfo> Tools { get; init; } = [];

        public Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(this.Tools);

        public Task<(string Text, bool IsError)> CallToolAsync(string toolName, System.Text.Json.JsonElement arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult((string.Empty, false));

        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);

        public Task<string> ReadResourceAsync(string uri, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);

        public Task<string> GetPromptAsync(string name, System.Text.Json.Nodes.JsonNode? arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
