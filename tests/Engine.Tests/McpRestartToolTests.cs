using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// Covers <see cref="FileMcpServerConfigSource"/> — the read model the restart tool uses to decide
/// whether a server is present and enabled — and <see cref="RestartMcpServerTool"/> itself: argument
/// validation, the "present and enabled" gate, the disconnect-then-reconnect sequence, and the
/// failure paths. One test drives a real <c>McpStdioTestServer</c> child process end to end, because
/// replacing a live process is the whole point of the tool.
/// </summary>
public sealed class FileMcpServerConfigSourceTests
{
    [Fact]
    public void LoadServers_includes_disabled_entries()
    {
        using var work = new RestartToolTempDir();
        using var user = new RestartToolTempDir();
        File.WriteAllText(Path.Combine(work.Path, ".mcp.json"),
            """{ "mcpServers": { "off": { "command": "x", "disabled": true }, "on": { "command": "y" } } }""");

        var servers = new FileMcpServerConfigSource(work.Path, user.Path).LoadServers();

        Assert.Equal(2, servers.Count);
        Assert.True(servers["off"].Disabled);
        Assert.False(servers["on"].Disabled);
    }

    [Fact]
    public void LoadServers_applies_plugin_then_user_then_project_precedence()
    {
        using var work = new RestartToolTempDir();
        using var user = new RestartToolTempDir();
        File.WriteAllText(Path.Combine(user.Path, ".mcp.json"),
            """{ "mcpServers": { "shared": { "command": "user" }, "user-only": { "command": "u" } } }""");
        File.WriteAllText(Path.Combine(work.Path, ".mcp.json"),
            """{ "mcpServers": { "shared": { "command": "project" } } }""");

        var plugins = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["shared"] = new McpStdioServerConfig("plugin", [], new Dictionary<string, string>()),
            ["plugin-only"] = new McpStdioServerConfig("plugin", [], new Dictionary<string, string>()),
        };

        var servers = new FileMcpServerConfigSource(work.Path, user.Path, pluginServers: plugins).LoadServers();

        Assert.Equal(3, servers.Count);
        Assert.Equal("project", Assert.IsType<McpStdioServerConfig>(servers["shared"]).Command);
        Assert.Equal("u", Assert.IsType<McpStdioServerConfig>(servers["user-only"]).Command);
        Assert.Equal("plugin", Assert.IsType<McpStdioServerConfig>(servers["plugin-only"]).Command);
    }

    [Fact]
    public void LoadServers_ignores_the_project_layer_when_excluded()
    {
        using var work = new RestartToolTempDir();
        using var user = new RestartToolTempDir();
        File.WriteAllText(Path.Combine(user.Path, ".mcp.json"),
            """{ "mcpServers": { "shared": { "command": "user" } } }""");
        File.WriteAllText(Path.Combine(work.Path, ".mcp.json"),
            """{ "mcpServers": { "shared": { "command": "project" }, "project-only": { "command": "p" } } }""");

        var servers = new FileMcpServerConfigSource(work.Path, user.Path, includeProject: false).LoadServers();

        Assert.Equal("user", Assert.IsType<McpStdioServerConfig>(Assert.Single(servers).Value).Command);
    }

    [Fact]
    public void LoadServers_reports_a_malformed_config_instead_of_returning_empty()
    {
        using var work = new RestartToolTempDir();
        using var user = new RestartToolTempDir();
        File.WriteAllText(Path.Combine(work.Path, ".mcp.json"), "{ not json");

        var source = new FileMcpServerConfigSource(work.Path, user.Path);

        Assert.Throws<McpException>(() => source.LoadServers());
    }

    [Fact]
    public void LoadServers_keeps_answering_when_one_sibling_entry_is_unparseable()
    {
        // The strict read model rejects the whole file over a legacy "sse" entry, but the connect
        // path merely skips it — so the healthy siblings must still be reported.
        using var work = new RestartToolTempDir();
        using var user = new RestartToolTempDir();
        File.WriteAllText(Path.Combine(work.Path, ".mcp.json"),
            """
            { "mcpServers": {
                "legacy": { "type": "sse", "url": "https://example.test/sse" },
                "good": { "command": "y" },
                "off": { "command": "z", "disabled": true } } }
            """);

        var servers = new FileMcpServerConfigSource(work.Path, user.Path).LoadServers();

        Assert.False(servers.ContainsKey("legacy"));
        Assert.False(servers["good"].Disabled);
        Assert.True(servers["off"].Disabled);
    }

    [Fact]
    public void LoadServers_reports_a_disabled_project_entry_shadowing_an_enabled_user_entry()
    {
        using var work = new RestartToolTempDir();
        using var user = new RestartToolTempDir();
        File.WriteAllText(Path.Combine(user.Path, ".mcp.json"),
            """{ "mcpServers": { "shared": { "command": "user" } } }""");
        File.WriteAllText(Path.Combine(work.Path, ".mcp.json"),
            """{ "mcpServers": { "shared": { "command": "project", "disabled": true } } }""");

        var servers = new FileMcpServerConfigSource(work.Path, user.Path).LoadServers();

        Assert.True(Assert.Single(servers).Value.Disabled);
    }

    [Fact]
    public void LoadServers_lets_an_enabled_user_entry_override_a_disabled_plugin_entry()
    {
        using var work = new RestartToolTempDir();
        using var user = new RestartToolTempDir();
        File.WriteAllText(Path.Combine(user.Path, ".mcp.json"),
            """{ "mcpServers": { "shared": { "command": "user" } } }""");
        var plugins = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["shared"] = new McpStdioServerConfig("plugin", [], new Dictionary<string, string>()) { Disabled = true },
        };

        var servers = new FileMcpServerConfigSource(work.Path, user.Path, pluginServers: plugins).LoadServers();

        Assert.False(Assert.Single(servers).Value.Disabled);
    }
}

public sealed class RestartMcpServerToolTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolContext FakeContext() => new(WorkingDirectory: ".");

    private static readonly IReadOnlyDictionary<string, McpServerConfig> NoServers =
        new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);

    // ── Metadata + argument validation ──────────────────────────────────────

    [Fact]
    public void Tool_has_expected_metadata()
    {
        var tool = new RestartMcpServerTool(new McpClientManager(), new StubConfigSource(NoServers));

        Assert.Equal("restart_mcp_server", tool.Name);

        // Auto-runs: restarting a process the session already launched is not a privileged action,
        // and the failure it repairs shows up mid-turn when nobody is watching.
        Assert.True(tool.IsReadOnly);
        Assert.Contains("\"required\": [\"server\"]", tool.InputSchemaJson);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"server":123}""")]
    [InlineData("""{"server":"   "}""")]
    public async Task Invalid_server_argument_is_an_error(string input)
    {
        var tool = new RestartMcpServerTool(new McpClientManager(), new StubConfigSource(NoServers));

        var result = await tool.ExecuteAsync(Json(input), FakeContext());

        Assert.True(result.IsError);
        Assert.Contains("Missing required argument: server", result.Content);
    }

    // ── The "present and enabled" gate ──────────────────────────────────────

    [Fact]
    public async Task Unknown_server_is_rejected_and_names_the_enabled_servers()
    {
        var (manager, factory) = BuildManager();
        await ConnectAsync(manager, "alpha");
        var source = new StubConfigSource(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["alpha"] = HttpConfig(),
            ["beta"] = HttpConfig(),
            ["hidden"] = HttpConfig() with { Disabled = true },
        });
        var tool = new RestartMcpServerTool(manager, source);

        var result = await tool.ExecuteAsync(Json("""{"server":"nope"}"""), FakeContext());

        Assert.True(result.IsError);
        Assert.Contains("'nope' is not configured", result.Content);
        Assert.Contains("alpha, beta", result.Content);
        Assert.DoesNotContain("hidden", result.Content);

        // The runtime must be untouched by a rejected request.
        Assert.True(manager.IsServerConnected("alpha"));
        Assert.Equal(1, factory.CreatedCount);
    }

    [Fact]
    public async Task Disabled_server_is_rejected_without_touching_the_runtime()
    {
        var (manager, factory) = BuildManager();
        await ConnectAsync(manager, "alpha");
        var source = new StubConfigSource(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["alpha"] = HttpConfig() with { Disabled = true },
        });
        var tool = new RestartMcpServerTool(manager, source);

        var result = await tool.ExecuteAsync(Json("""{"server":"alpha"}"""), FakeContext());

        Assert.True(result.IsError);
        Assert.Contains("is disabled", result.Content);
        Assert.True(manager.IsServerConnected("alpha"));
        Assert.Equal(1, factory.CreatedCount);
    }

    [Fact]
    public async Task Unreadable_config_is_reported_as_an_error()
    {
        var tool = new RestartMcpServerTool(
            new McpClientManager(),
            new StubConfigSource(NoServers) { Throw = new McpException("MCP config 'x' must contain valid JSON.") });

        var result = await tool.ExecuteAsync(Json("""{"server":"alpha"}"""), FakeContext());

        Assert.True(result.IsError);
        Assert.Contains("Cannot read the MCP configuration", result.Content);
    }

    // ── The restart itself ──────────────────────────────────────────────────

    [Fact]
    public async Task Restart_replaces_the_client_and_reports_the_tool_count()
    {
        var (manager, factory) = BuildManager(client => client.Tools = [new McpToolInfo("echo", "d", "{}", true)]);
        await ConnectAsync(manager, "alpha");
        var first = Assert.Single(manager.Clients);
        var source = new StubConfigSource(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["alpha"] = HttpConfig(),
        });
        var tool = new RestartMcpServerTool(manager, source);

        var result = await tool.ExecuteAsync(Json("""{"server":"alpha"}"""), FakeContext());

        Assert.False(result.IsError);
        Assert.Contains("Restarted MCP server 'alpha': 1 tool available.", result.Content);
        Assert.Equal(2, factory.CreatedCount);
        Assert.True(((FakeRestartClient)first).Disposed);
        Assert.NotSame(first, Assert.Single(manager.Clients));
        Assert.Single(manager.ServerTools("alpha"));
    }

    [Fact]
    public async Task Restart_of_a_stopped_server_starts_it_and_says_so()
    {
        var (manager, _) = BuildManager();
        await ConnectAsync(manager, "alpha");
        Assert.True(await manager.DisconnectServerAsync("alpha"));
        var source = new StubConfigSource(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["alpha"] = HttpConfig(),
        });
        var tool = new RestartMcpServerTool(manager, source);

        var result = await tool.ExecuteAsync(Json("""{"server":"alpha"}"""), FakeContext());

        Assert.False(result.IsError);
        Assert.Contains("Started MCP server 'alpha'", result.Content);
        Assert.Contains("was not running", result.Content);
        Assert.True(manager.IsServerConnected("alpha"));
    }

    [Fact]
    public async Task Failed_reconnect_reports_the_error_and_leaves_the_server_stopped()
    {
        var (manager, factory) = BuildManager();
        await ConnectAsync(manager, "alpha");
        factory.FailNextInitializeWith = "server exploded";
        var source = new StubConfigSource(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["alpha"] = HttpConfig(),
        });
        var tool = new RestartMcpServerTool(manager, source);

        var result = await tool.ExecuteAsync(Json("""{"server":"alpha"}"""), FakeContext());

        Assert.True(result.IsError);
        Assert.Contains("Failed to restart MCP server 'alpha'", result.Content);
        Assert.Contains("server exploded", result.Content);
        Assert.Contains("now stopped", result.Content);
        Assert.False(manager.IsServerConnected("alpha"));
        Assert.True(manager.HasFailedConnections);
    }

    [Fact]
    public async Task Restart_reuses_the_configuration_the_session_connected_with()
    {
        // The gate reads .mcp.json, but the relaunch must not: otherwise anything able to write that
        // file mid-session could turn a restart into a launcher for a command of its choosing.
        var (manager, factory) = BuildManager();
        await ConnectAsync(manager, "alpha");
        var tampered = new McpStdioServerConfig("evil.exe", ["--pwn"], new Dictionary<string, string>());
        var source = new StubConfigSource(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["alpha"] = tampered,
        });
        var tool = new RestartMcpServerTool(manager, source);

        var result = await tool.ExecuteAsync(Json("""{"server":"alpha"}"""), FakeContext());

        Assert.False(result.IsError);

        // Still the original HTTP transport: the stdio command in the config was never used.
        Assert.Equal(2, factory.CreatedCount);
        Assert.Equal(new Uri("https://example.test/mcp"), factory.LastConfig!.Url);
    }

    [Fact]
    public async Task A_server_never_started_in_this_session_is_refused()
    {
        var (manager, factory) = BuildManager();
        var source = new StubConfigSource(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["alpha"] = HttpConfig(),
        });
        var tool = new RestartMcpServerTool(manager, source);

        var result = await tool.ExecuteAsync(Json("""{"server":"alpha"}"""), FakeContext());

        Assert.True(result.IsError);
        Assert.Contains("has not been started in this session", result.Content);
        Assert.Equal(0, factory.CreatedCount);
    }

    [Fact]
    public async Task Concurrent_restarts_of_the_same_server_do_not_interleave()
    {
        var (manager, factory) = BuildManager();
        await ConnectAsync(manager, "alpha");
        var source = new StubConfigSource(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["alpha"] = HttpConfig(),
        });
        var tool = new RestartMcpServerTool(manager, source);

        var results = await Task.WhenAll(
            tool.ExecuteAsync(Json("""{"server":"alpha"}"""), FakeContext()),
            tool.ExecuteAsync(Json("""{"server":"alpha"}"""), FakeContext()));

        // Both succeed and both report the server as previously running: neither observed the other
        // mid-restart, and the runtime ends with exactly one live client.
        Assert.All(results, r => Assert.False(r.IsError));
        Assert.All(results, r => Assert.Contains("Restarted MCP server 'alpha'", r.Content));
        Assert.Equal(3, factory.CreatedCount);
        Assert.Single(manager.Clients);
    }

    [Fact]
    public async Task A_server_name_carrying_terminal_escapes_is_sanitized_in_the_error()
    {
        var tool = new RestartMcpServerTool(new McpClientManager(), new StubConfigSource(NoServers));

        var result = await tool.ExecuteAsync(Json("""{"server":"a\u001b[2Jb"}"""), FakeContext());

        Assert.True(result.IsError);
        Assert.DoesNotContain('\u001b', result.Content);
    }

    // ── Live tool list ──────────────────────────────────────────────────────

    [Fact]
    public async Task Session_tool_list_follows_the_manager_across_a_restart()
    {
        // serve captures SessionOptions.ExtraTools once but re-enumerates it every turn. A frozen
        // snapshot would keep handing the model wrappers bound to the client the restart disposed.
        var (manager, _) = BuildManager(client => client.Tools = [new McpToolInfo("echo", "d", "{}", true)]);
        await ConnectAsync(manager, "alpha");
        var helper = new ListMcpResourcesTool(manager);
        IReadOnlyList<ITool> live = new McpSessionToolList(manager, [helper]);

        var before = Assert.Single(live.OfType<McpTool>());
        Assert.Equal(2, live.Count);

        var source = new StubConfigSource(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["alpha"] = HttpConfig(),
        });
        Assert.False((await new RestartMcpServerTool(manager, source)
            .ExecuteAsync(Json("""{"server":"alpha"}"""), FakeContext())).IsError);

        var after = Assert.Single(live.OfType<McpTool>());
        Assert.Equal(2, live.Count);
        Assert.NotSame(before, after);
        Assert.Same(helper, live[^1]);
    }

    [Fact]
    public async Task Session_tool_list_drops_the_tools_of_a_server_that_fails_to_come_back()
    {
        var (manager, factory) = BuildManager(client => client.Tools = [new McpToolInfo("echo", "d", "{}", true)]);
        await ConnectAsync(manager, "alpha");
        IReadOnlyList<ITool> live = new McpSessionToolList(manager, [new ListMcpResourcesTool(manager)]);
        Assert.Equal(2, live.Count);

        factory.FailNextInitializeWith = "boom";
        var source = new StubConfigSource(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["alpha"] = HttpConfig(),
        });
        Assert.True((await new RestartMcpServerTool(manager, source)
            .ExecuteAsync(Json("""{"server":"alpha"}"""), FakeContext())).IsError);

        Assert.Empty(live.OfType<McpTool>());
        Assert.Single(live);
    }

    // ── Recovery for the tools the running turn already holds ───────────────

    [Fact]
    public async Task A_tool_wrapper_from_before_the_restart_reaches_the_replacement_client()
    {
        var (manager, _) = BuildManager(client => client.Tools = [new McpToolInfo("echo", "d", "{}", true)]);
        await ConnectAsync(manager, "alpha");

        // What the model is actually holding when it restarts: the registry for the running turn was
        // built before the restart, so the retry that follows goes through THESE wrappers.
        var beforeRestart = Assert.Single(manager.ServerTools("alpha"));
        var firstClient = Assert.IsType<FakeRestartClient>(Assert.Single(manager.Clients));

        Assert.False((await RestartToolFor(manager).ExecuteAsync(Json("""{"server":"alpha"}"""), FakeContext())).IsError);

        var secondClient = Assert.IsType<FakeRestartClient>(Assert.Single(manager.Clients));
        Assert.NotSame(firstClient, secondClient);

        var result = await beforeRestart.ExecuteAsync(Json("{}"), FakeContext());

        Assert.False(result.IsError);
        Assert.Equal(secondClient.Identity, result.Content);
        Assert.Equal(0, firstClient.CallCount);
    }

    [Fact]
    public async Task A_tool_wrapper_for_a_stopped_server_reports_it_instead_of_calling_a_dead_client()
    {
        var (manager, _) = BuildManager(client => client.Tools = [new McpToolInfo("echo", "d", "{}", true)]);
        await ConnectAsync(manager, "alpha");
        var beforeStop = Assert.Single(manager.ServerTools("alpha"));
        var client = Assert.IsType<FakeRestartClient>(Assert.Single(manager.Clients));

        Assert.True(await manager.DisconnectServerAsync("alpha"));

        var result = await beforeStop.ExecuteAsync(Json("{}"), FakeContext());

        Assert.True(result.IsError);
        Assert.Contains("alpha", result.Content, StringComparison.Ordinal);
        Assert.Contains("restart_mcp_server", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, client.CallCount);
    }

    // ── Real child process ──────────────────────────────────────────────────

    [Fact]
    public async Task Restart_replaces_a_real_stdio_server_process()
    {
        await using var manager = new McpClientManager();
        var config = StdioTestServerConfig();
        var connect = await manager.ConnectServerAsync("stdio-srv", config);
        Assert.True(connect.Connected);
        var first = Assert.Single(manager.Clients);

        var source = new StubConfigSource(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["stdio-srv"] = config,
        });
        var tool = new RestartMcpServerTool(manager, source);

        var result = await tool.ExecuteAsync(Json("""{"server":"stdio-srv"}"""), FakeContext());

        Assert.False(result.IsError);
        Assert.Contains("Restarted MCP server 'stdio-srv'", result.Content);

        // A brand new client (hence a brand new child process) now serves the same name.
        var second = Assert.Single(manager.Clients);
        Assert.NotSame(first, second);
        Assert.Contains(manager.ServerTools("stdio-srv"), t => t.Name.EndsWith("echo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_real_stdio_tool_from_before_the_restart_runs_against_the_replacement_process()
    {
        await using var manager = new McpClientManager();
        var config = StdioTestServerConfig();
        Assert.True((await manager.ConnectServerAsync("stdio-srv", config)).Connected);

        // The wrapper the running turn already handed to the model, and the process behind it.
        var beforeRestart = Assert.Single(manager.ServerTools("stdio-srv"));
        var firstPid = Assert.IsType<McpStdioClient>(Assert.Single(manager.Clients)).ProcessId;
        Assert.NotNull(firstPid);
        Assert.Equal($"pid:{firstPid}", (await beforeRestart.ExecuteAsync(Json("{}"), FakeContext())).Content);

        var source = new StubConfigSource(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["stdio-srv"] = config,
        });
        Assert.False((await new RestartMcpServerTool(manager, source)
            .ExecuteAsync(Json("""{"server":"stdio-srv"}"""), FakeContext())).IsError);

        var secondPid = Assert.IsType<McpStdioClient>(Assert.Single(manager.Clients)).ProcessId;
        Assert.NotNull(secondPid);
        Assert.NotEqual(firstPid, secondPid);

        // Exactly what the model does after restarting: retry through the wrapper it already has.
        var retry = await beforeRestart.ExecuteAsync(Json("{}"), FakeContext());

        Assert.False(retry.IsError);
        Assert.Equal($"pid:{secondPid}", retry.Content);
        Assert.True(HasExited(firstPid.Value));
    }

    [Fact]
    public async Task Disposing_a_stdio_client_leaves_no_live_child_behind()
    {
        var client = new McpStdioClient("stdio-srv", StdioTestServerConfig());
        await client.InitializeAndListToolsAsync();
        var pid = client.ProcessId;
        Assert.NotNull(pid);

        await client.DisposeAsync();

        // The replacement must never race a child that still holds the old port, lock file or
        // database — that is what turned a restart into a server that stays broken until Coda exits.
        Assert.True(HasExited(pid.Value));
    }

    [Fact]
    public async Task Calling_a_tool_on_a_disposed_stdio_client_fails_instead_of_hanging()
    {
        var client = new McpStdioClient("stdio-srv", StdioTestServerConfig());
        await client.InitializeAndListToolsAsync();
        await client.DisposeAsync();

        var call = client.CallToolAsync("echo", Json("{}"));
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10)));

        // Writing into the killed child's pipe never gets an answer, so without an explicit closed
        // state this waits out the whole 10-minute MCP tool timeout.
        Assert.Same(call, finished);
        await Assert.ThrowsAsync<McpException>(() => call);
    }

    [Fact]
    public async Task Disposing_a_stdio_client_fails_the_calls_that_are_still_in_flight()
    {
        // A connection nothing ever answers: the request stays pending until teardown decides its
        // fate, which is exactly the state a hung server leaves a call in.
        var client = new McpStdioClient("stdio-srv", new McpRpcConnection(TextWriter.Null));

        var pending = client.CallToolAsync("never-answered", Json("{}"));
        Assert.False(pending.IsCompleted);

        await client.DisposeAsync();

        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(pending, finished);
        await Assert.ThrowsAsync<McpException>(() => pending);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            // Already reaped by the OS: there is no process with that id any more.
            return true;
        }
    }

    private static RestartMcpServerTool RestartToolFor(McpClientManager manager) =>
        new(manager, new StubConfigSource(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["alpha"] = HttpConfig(),
        }));

    private static McpHttpServerConfig HttpConfig() =>
        new(new Uri("https://example.test/mcp"), new Dictionary<string, string>(StringComparer.Ordinal), McpAuthConfig.Default);

    private static (McpClientManager Manager, FakeHttpClientFactory Factory) BuildManager(
        Action<FakeRestartClient>? configure = null)
    {
        var factory = new FakeHttpClientFactory(configure);
        return (new McpClientManager(factory), factory);
    }

    private static async Task ConnectAsync(McpClientManager manager, string name)
    {
        var result = await manager.ConnectServerAsync(name, HttpConfig());
        Assert.True(result.Connected);
    }

    private static McpStdioServerConfig StdioTestServerConfig()
    {
        // AppContext.BaseDirectory: .../tests/Engine.Tests/bin/<config>/<tfm>/
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = baseDir.Name;
        var configuration = baseDir.Parent!.Name;
        var testsDir = baseDir.Parent!.Parent!.Parent!.Parent!;
        var serverDir = Path.Combine(testsDir.FullName, "McpStdioTestServer", "bin", configuration, tfm);

        var exe = Path.Combine(serverDir, "McpStdioTestServer.exe");
        return File.Exists(exe)
            ? new McpStdioServerConfig(exe, ["serve"], new Dictionary<string, string>())
            : new McpStdioServerConfig(
                "dotnet",
                [Path.Combine(serverDir, "McpStdioTestServer.dll"), "serve"],
                new Dictionary<string, string>());
    }

    private sealed class StubConfigSource(IReadOnlyDictionary<string, McpServerConfig> servers) : IMcpServerConfigSource
    {
        public Exception? Throw { get; init; }

        public IReadOnlyDictionary<string, McpServerConfig> LoadServers() =>
            this.Throw is { } error ? throw error : servers;
    }

    private sealed class FakeHttpClientFactory(Action<FakeRestartClient>? configure) : IMcpHttpClientFactory
    {
        public int CreatedCount { get; private set; }

        public McpHttpServerConfig? LastConfig { get; private set; }

        public string? FailNextInitializeWith { get; set; }

        public IMcpClient Create(string serverName, McpHttpServerConfig config)
        {
            this.CreatedCount++;
            this.LastConfig = config;
            var client = new FakeRestartClient(serverName) { ThrowOnInit = this.FailNextInitializeWith };
            this.FailNextInitializeWith = null;
            configure?.Invoke(client);
            return client;
        }
    }

    private sealed class FakeRestartClient(string serverName) : IMcpClient
    {
        private static int instances;

        private readonly int instance = Interlocked.Increment(ref instances);

        public string ServerName => serverName;

        public IReadOnlyList<McpToolInfo> Tools { get; set; } = [];

        public string? ThrowOnInit { get; init; }

        public bool Disposed { get; private set; }

        /// <summary>Identifies this instance in a call result, so a test can tell which client served it.</summary>
        public string Identity => $"{serverName}#{this.instance}";

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken cancellationToken = default) =>
            this.ThrowOnInit is { } message ? throw new McpException(message) : Task.FromResult(this.Tools);

        public Task<(string Text, bool IsError)> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return Task.FromResult((this.Identity, false));
        }

        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);

        public Task<string> ReadResourceAsync(string uri, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);

        public Task<string> GetPromptAsync(string name, JsonNode? arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class RestartToolTempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coda-mcp-restart-" + Guid.NewGuid().ToString("N"));

    public RestartToolTempDir() => Directory.CreateDirectory(this.Path);

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
