using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// End-to-end behaviour of the schema policy through <see cref="McpClientManager"/>: a server
/// shipping malformed schemas must cost the user that server's tools at worst, never the session,
/// and never silently.
/// </summary>
public sealed class McpSchemaPolicyConnectTests
{
    private const string Typeless = """{"$schema":"http://json-schema.org/draft-07/schema#"}""";

    private static McpToolInfo Coerced(string name) =>
        Assert.Single(McpToolInfo.ParseList(
            JsonDocument.Parse($$"""{"tools":[{"name":"{{name}}","inputSchema":{{Typeless}}}]}""").RootElement));

    private static McpToolInfo Valid(string name) =>
        Assert.Single(McpToolInfo.ParseList(
            JsonDocument.Parse($$$"""{"tools":[{"name":"{{{name}}}","inputSchema":{"type":"object"}}]}""").RootElement));

    private static McpClientManager Manager(McpSchemaPolicy policy) => new([], schemaPolicy: policy);

    [Fact]
    public async Task Coerce_connects_and_keeps_every_tool()
    {
        var client = new FakeClient("playwright") { Tools = [Coerced("browser_navigate"), Valid("browser_close")] };
        await using var manager = Manager(McpSchemaPolicy.Coerce);

        var result = await manager.ConnectClientAsync(client, default);

        Assert.True(result.Connected);
        Assert.Equal(2, result.ToolCount);
        Assert.Equal(2, manager.Tools.Count);
    }

    [Fact]
    public async Task Coerce_reports_the_repair_to_the_user()
    {
        var client = new FakeClient("playwright") { Tools = [Coerced("a"), Valid("b")] };
        await using var manager = Manager(McpSchemaPolicy.Coerce);

        var result = await manager.ConnectClientAsync(client, default);

        Assert.NotNull(result.SchemaWarning);
        Assert.Contains("playwright", result.SchemaWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_healthy_server_produces_no_warning()
    {
        var client = new FakeClient("fs") { Tools = [Valid("read"), Valid("write")] };
        await using var manager = Manager(McpSchemaPolicy.Coerce);

        var result = await manager.ConnectClientAsync(client, default);

        Assert.Null(result.SchemaWarning);
    }

    [Fact]
    public async Task Skip_drops_the_offending_tool_but_keeps_the_server()
    {
        var client = new FakeClient("playwright") { Tools = [Coerced("bad"), Valid("good")] };
        await using var manager = Manager(McpSchemaPolicy.Skip);

        var result = await manager.ConnectClientAsync(client, default);

        Assert.True(result.Connected);
        Assert.Equal(1, result.ToolCount);
        Assert.Equal("mcp__playwright__good", Assert.Single(manager.Tools).Name);

        // The drop leaves no trace in the tool list, so the warning is the ONLY way the user can
        // learn about it — and it must say "dropped", not "coerced".
        Assert.NotNull(result.SchemaWarning);
        Assert.Contains("dropped", result.SchemaWarning, StringComparison.Ordinal);
        Assert.Equal(result.SchemaWarning, manager.SchemaWarningFor("playwright"));
    }

    [Fact]
    public async Task The_warning_outlives_the_connect_log_for_the_ui()
    {
        var client = new FakeClient("playwright") { Tools = [Coerced("a")] };
        await using var manager = Manager(McpSchemaPolicy.Coerce);

        await manager.ConnectClientAsync(client, default);

        Assert.Contains("playwright", manager.SchemaWarningFor("playwright")!, StringComparison.Ordinal);
        Assert.Null(manager.SchemaWarningFor("some-other-server"));
    }

    [Fact]
    public async Task A_healthy_server_records_no_warning()
    {
        var client = new FakeClient("fs") { Tools = [Valid("read")] };
        await using var manager = Manager(McpSchemaPolicy.Coerce);

        await manager.ConnectClientAsync(client, default);

        Assert.Null(manager.SchemaWarningFor("fs"));
    }

    [Fact]
    public async Task Disconnecting_clears_the_warning()
    {
        var client = new FakeClient("playwright") { Tools = [Coerced("a")] };
        await using var manager = Manager(McpSchemaPolicy.Coerce);
        await manager.ConnectClientAsync(client, default);

        Assert.True(await manager.DisconnectServerAsync("playwright"));

        Assert.Null(manager.SchemaWarningFor("playwright"));
    }

    [Fact]
    public async Task A_hostile_server_name_cannot_inject_escapes_through_the_warning()
    {
        var client = new FakeClient("evil\u001b[31m\nspoof") { Tools = [Coerced("a")] };
        await using var manager = Manager(McpSchemaPolicy.Coerce);

        var result = await manager.ConnectClientAsync(client, default);

        var warning = Assert.IsType<string>(result.SchemaWarning);
        Assert.DoesNotContain('\u001b', warning);
        Assert.DoesNotContain('\n', warning);
    }

    [Fact]
    public async Task ConnectAll_scrubs_a_hostile_server_name_from_its_log_lines()
    {
        // The name is an unvalidated .mcp.json key and several hosts pipe this log straight to
        // Console.Error, so an escape sequence here would reach the terminal.
        var logs = new List<string>();
        await using var manager = Manager(McpSchemaPolicy.Coerce);

        // An HTTP server with no factory fails cleanly ("HTTP transport is not available."), which
        // exercises the log line without spawning a process.
        await manager.ConnectAllAsync(
            new Dictionary<string, McpServerConfig>
            {
                ["evil\u001b[2J\nspoof"] = new McpHttpServerConfig(
                    new Uri("https://example.test/mcp"),
                    new Dictionary<string, string>(),
                    new McpAuthConfig(McpAuthMode.None, null, null, null)),
            },
            logs.Add,
            default);

        Assert.NotEmpty(logs);
        Assert.All(logs, line =>
        {
            Assert.DoesNotContain('\u001b', line);
            Assert.DoesNotContain('\n', line);
        });
    }

    [Fact]
    public async Task Strict_refuses_the_server_and_disposes_its_client()
    {
        var client = new FakeClient("playwright") { Tools = [Coerced("bad"), Valid("good")] };
        await using var manager = Manager(McpSchemaPolicy.Strict);

        var result = await manager.ConnectClientAsync(client, default);

        Assert.False(result.Connected);
        Assert.Contains("playwright", result.Error!, StringComparison.Ordinal);
        Assert.Empty(manager.Tools);
        Assert.True(client.Disposed);
        Assert.False(manager.IsServerConnected("playwright"));
    }

    [Fact]
    public async Task Strict_accepts_a_server_whose_schemas_are_all_valid()
    {
        var client = new FakeClient("fs") { Tools = [Valid("read")] };
        await using var manager = Manager(McpSchemaPolicy.Strict);

        var result = await manager.ConnectClientAsync(client, default);

        Assert.True(result.Connected);
        Assert.Single(manager.Tools);
    }

    [Fact]
    public async Task The_coerced_flag_reaches_the_tool_wrapper_and_its_schema_is_usable()
    {
        var client = new FakeClient("playwright") { Tools = [Coerced("browser_navigate")] };
        await using var manager = Manager(McpSchemaPolicy.Coerce);

        await manager.ConnectClientAsync(client, default);

        var tool = Assert.Single(manager.ServerTools("playwright"));
        Assert.True(tool.SchemaCoerced);

        // The whole point: what we would put on the wire is now acceptable to the model APIs.
        var schema = JsonNode.Parse(tool.InputSchemaJson)!.AsObject();
        Assert.Equal("object", schema["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Every_tool_broken_hints_at_a_bad_package_version()
    {
        var client = new FakeClient("playwright") { Tools = [Coerced("a"), Coerced("b")] };
        await using var manager = Manager(McpSchemaPolicy.Coerce);

        var result = await manager.ConnectClientAsync(client, default);

        Assert.Contains("version", result.SchemaWarning!, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeClient(string serverName) : IMcpClient
    {
        public string ServerName { get; } = serverName;

        public IReadOnlyList<McpToolInfo> Tools { get; init; } = [];

        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(this.Tools);

        public Task<(string Text, bool IsError)> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct = default) =>
            Task.FromResult((string.Empty, false));

        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);

        public Task<string> ReadResourceAsync(string uri, CancellationToken ct = default) => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);

        public Task<string> GetPromptAsync(string name, JsonNode? arguments, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
