using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// Lifecycle/adoption behavior of <see cref="McpClientManager"/>: a successful connect adopts a
/// client and its tools atomically (all-or-nothing) and bumps <see cref="McpClientManager.Version"/>
/// exactly once; any failure disposes the client exactly once and leaves no partial registration.
/// The manager owns a linked connect-timeout token whose classification precedence is
/// caller-cancel &gt; manager-timeout &gt; typed connection error &gt; unclassified cancellation.
/// </summary>
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
    public async Task ConnectClientAsync_adopts_all_tools_atomically_with_one_version_bump()
    {
        var manager = new McpClientManager();
        var client = new FakeMcpClient("srv")
        {
            Tools =
            [
                new McpToolInfo("a", "d", "{}", true),
                new McpToolInfo("b", "d", "{}", false),
            ],
        };

        var before = manager.Version;
        var result = await manager.ConnectClientAsync(client, default);

        Assert.True(result.Connected);
        Assert.Equal(2, result.ToolCount);
        Assert.Equal(2, manager.ServerTools("srv").Count);
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
    public async Task ConnectClientAsync_failure_records_safe_error_and_a_later_success_clears_it()
    {
        var manager = new McpClientManager();
        const string secret = "Bearer abcdefghijklmnopqrstuvwxyz123456";
        var failed = new FakeMcpClient("bad")
        {
            ThrowOnInit = $"upstream rejected https://user:password@example.test/mcp?token=raw-secret; {secret}; MCP_TOKEN=env-secret",
        };

        var result = await manager.ConnectClientAsync(failed, default);

        Assert.False(result.Connected);
        Assert.False(manager.IsServerConnected("bad"));
        Assert.Empty(manager.ServerTools("bad"));
        Assert.NotNull(manager.LastConnectionErrorFor("bad"));
        Assert.DoesNotContain("raw-secret", result.Error!);
        Assert.DoesNotContain("env-secret", result.Error!);
        Assert.DoesNotContain(secret, result.Error!);
        Assert.DoesNotContain("raw-secret", manager.LastConnectionErrorFor("bad")!);
        Assert.DoesNotContain("env-secret", manager.LastConnectionErrorFor("bad")!);
        Assert.DoesNotContain(secret, manager.LastConnectionErrorFor("bad")!);

        var recovered = new FakeMcpClient("bad") { ThrowOnPromptList = "prompt list failed" };
        Assert.True((await manager.ConnectClientAsync(recovered, default)).Connected);
        Assert.Null(manager.LastConnectionErrorFor("bad"));
        Assert.Empty(await manager.ServerPromptsAsync("bad"));
        Assert.NotNull(manager.LastConnectionErrorFor("bad"));
        Assert.True(await manager.DisconnectServerAsync("bad"));
        Assert.Null(manager.LastConnectionErrorFor("bad"));
    }

    [Fact]
    public async Task ConnectClientAsync_failure_redacts_bare_secret_assignments()
    {
        var manager = new McpClientManager();
        var client = new FakeMcpClient("bad")
        {
            ThrowOnInit = "connection failed: token=bare-token-value secret:bare-secret-value password=\"bare password value\" api_key:bare-api-key-value apikey=bare-apikey-value",
        };

        var result = await manager.ConnectClientAsync(client, default);

        Assert.Contains("connection failed", result.Error!);
        Assert.DoesNotContain("bare-token-value", result.Error!);
        Assert.DoesNotContain("bare-secret-value", result.Error!);
        Assert.DoesNotContain("bare password value", result.Error!);
        Assert.DoesNotContain("password value", result.Error!);
        Assert.DoesNotContain("bare-api-key-value", result.Error!);
        Assert.DoesNotContain("bare-apikey-value", result.Error!);
    }

    [Fact]
    public async Task ConnectClientAsync_failure_redacts_prefixed_secret_assignments()
    {
        var manager = new McpClientManager();
        var client = new FakeMcpClient("bad")
        {
            ThrowOnInit = "connection failed: MCP_TOKEN=prefixed-mcp-token github_token:prefixed-github-token mySecret=prefixed-secret client_secret:prefixed-client-secret",
        };

        var result = await manager.ConnectClientAsync(client, default);

        Assert.DoesNotContain("prefixed-mcp-token", result.Error!);
        Assert.DoesNotContain("prefixed-github-token", result.Error!);
        Assert.DoesNotContain("prefixed-secret", result.Error!);
        Assert.DoesNotContain("prefixed-client-secret", result.Error!);
    }

    [Fact]
    public async Task ConnectClientAsync_failure_sanitizes_controls_to_a_single_line_before_storing()
    {
        var manager = new McpClientManager();
        var client = new FakeMcpClient("bad")
        {
            ThrowOnInit = "first line\r\n\u001b[31msecond\tline\u001b]0;spoofed title\u009C\u0001 to\rken=multiline-token-value\u202E",
        };

        var result = await manager.ConnectClientAsync(client, default);

        Assert.Equal("first line second line to ken=***redacted***", result.Error);
        Assert.DoesNotContain("multiline-token-value", result.Error!);
        Assert.All(result.Error!, ch => Assert.False(char.IsControl(ch)));
        Assert.DoesNotContain('\u202E', result.Error!);
    }

    [Fact]
    public async Task ConnectClientAsync_failure_preserves_non_secret_token_words()
    {
        var manager = new McpClientManager();
        var client = new FakeMcpClient("bad") { ThrowOnInit = "tokenization failed" };

        var result = await manager.ConnectClientAsync(client, default);

        Assert.Equal("tokenization failed", result.Error);
    }

    [Fact]
    public async Task DisconnectServerAsync_dispose_failure_removes_server_bumps_version_and_records_safe_error()
    {
        var manager = new McpClientManager();
        var client = new FakeMcpClient("bad")
        {
            Tools = [new McpToolInfo("echo", "d", "{}", true)],
            ThrowOnDispose = "failed to close https://example.test/mcp?access_token=dispose-secret",
        };
        Assert.True((await manager.ConnectClientAsync(client, default)).Connected);
        var before = manager.Version;

        var removed = await manager.DisconnectServerAsync("bad");

        Assert.True(removed);
        Assert.False(manager.IsServerConnected("bad"));
        Assert.Empty(manager.ServerTools("bad"));
        Assert.Equal(before + 1, manager.Version);
        Assert.DoesNotContain("dispose-secret", manager.LastConnectionErrorFor("bad")!);

        Assert.True((await manager.ConnectClientAsync(new FakeMcpClient("bad"), default)).Connected);
        Assert.Null(manager.LastConnectionErrorFor("bad"));
    }

    [Fact]
    public async Task LastConnectionErrorFor_is_isolated_by_exact_server_name_and_absent_disconnect_does_not_create_an_error()
    {
        var manager = new McpClientManager();

        await manager.ConnectClientAsync(new FakeMcpClient("one") { ThrowOnInit = "one failed" }, default);
        await manager.ConnectClientAsync(new FakeMcpClient("two") { ThrowOnInit = "two failed" }, default);

        Assert.Contains("one failed", manager.LastConnectionErrorFor("one")!);
        Assert.Contains("two failed", manager.LastConnectionErrorFor("two")!);
        Assert.Null(manager.LastConnectionErrorFor("ONE"));
        Assert.False(await manager.DisconnectServerAsync("absent"));
        Assert.Null(manager.LastConnectionErrorFor("absent"));
    }

    [Fact]
    public async Task ConnectClientAsync_failed_initialize_disposes_exactly_once_and_leaves_no_state()
    {
        var manager = new McpClientManager();
        var before = manager.Version;
        var client = new FakeMcpClient("bad") { ThrowOnInit = "boom" };

        var result = await manager.ConnectClientAsync(client, default);

        Assert.False(result.Connected);
        Assert.Equal(1, client.DisposeCount);
        Assert.False(manager.IsServerConnected("bad"));
        Assert.Empty(manager.Tools);
        Assert.Equal(before, manager.Version);
    }

    [Fact]
    public async Task ConnectClientAsync_wrapper_creation_failure_disposes_once_and_leaves_no_partial_state()
    {
        var manager = new McpClientManager();
        var before = manager.Version;

        // Smallest existing seam: a tool list whose element is null makes the McpTool wrapper
        // constructor throw (ArgumentNullException) after initialize succeeded, so adoption must
        // roll back entirely — no client, no tools, no version bump.
        var client = new FakeMcpClient("bad") { Tools = [null!] };

        var result = await manager.ConnectClientAsync(client, default);

        Assert.False(result.Connected);
        Assert.Equal(1, client.DisposeCount);
        Assert.False(manager.IsServerConnected("bad"));
        Assert.Empty(manager.Tools);
        Assert.Empty(manager.ServerTools("bad"));
        Assert.Equal(before, manager.Version);
    }

    [Fact]
    public async Task ConnectClientAsync_caller_cancellation_is_classified_and_disposes_once()
    {
        var manager = new McpClientManager();
        using var caller = new CancellationTokenSource();
        var before = manager.Version;
        var client = new FakeMcpClient("gen")
        {
            InitOverride = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return [];
            },
        };

        var task = manager.ConnectClientAsync(client, caller.Token);
        await caller.CancelAsync();
        var result = await task;

        Assert.False(result.Connected);
        Assert.Contains("was canceled", result.Error!);
        Assert.DoesNotContain("timed out", result.Error!);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(before, manager.Version);
        Assert.Empty(manager.Tools);
    }

    [Fact]
    public async Task ConnectClientAsync_manager_timeout_is_classified_for_generic_client()
    {
        var manager = new McpClientManager([], TimeSpan.FromMilliseconds(50));
        var before = manager.Version;
        var client = new FakeMcpClient("gen")
        {
            InitOverride = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return [];
            },
        };

        var result = await manager.ConnectClientAsync(client, default);

        Assert.False(result.Connected);
        Assert.Contains("timed out during initialize/tools/list", result.Error!);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(before, manager.Version);
        Assert.Empty(manager.Tools);
    }

    [Fact]
    public async Task ConnectClientAsync_manager_timeout_preserves_typed_startup_phase()
    {
        // A stdio client that never gets a response wraps the timeout-driven OperationCanceledException
        // into a typed McpConnectionException with Phase "initialize"; the manager must reclassify it
        // to a timeout message while preserving that phase (never the default "initialize/tools/list").
        var manager = new McpClientManager([], TimeSpan.FromMilliseconds(50));
        var rpc = new McpRpcConnection(new StringWriter());
        var client = new ScriptedMcpStdioClient("srv", rpc);

        var result = await manager.ConnectClientAsync(client, default);

        Assert.False(result.Connected);
        Assert.Contains("timed out during initialize after", result.Error!);
        Assert.DoesNotContain("initialize/tools/list", result.Error!);
    }

    [Fact]
    public async Task ConnectClientAsync_caller_cancellation_wins_when_both_requested()
    {
        var manager = new McpClientManager([], TimeSpan.FromMilliseconds(30));
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeMcpClient("both")
        {
            InitOverride = async ct =>
            {
                started.SetResult();
                await release.Task.ConfigureAwait(false); // ignore the token while blocked
                ct.ThrowIfCancellationRequested();
                return [];
            },
        };

        var task = manager.ConnectClientAsync(client, caller.Token);
        await started.Task;
        await caller.CancelAsync();
        await Task.Delay(80); // let the 30ms manager-timeout deadline also pass
        release.SetResult();
        var result = await task;

        Assert.False(result.Connected);
        Assert.Contains("was canceled", result.Error!);
        Assert.DoesNotContain("timed out", result.Error!);
    }

    [Fact]
    public async Task ConnectClientAsync_typed_connection_error_message_is_preserved()
    {
        var manager = new McpClientManager();
        var client = new FakeMcpClient("srv")
        {
            InitOverride = _ => throw McpConnectionException.ProcessExited("srv", "initialize", exitCode: 3, stderr: null),
        };

        var result = await manager.ConnectClientAsync(client, default);

        Assert.False(result.Connected);
        Assert.Equal("MCP server 'srv' exited during initialize with exit code 3.", result.Error);
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task ConnectClientAsync_unsupported_large_timeout_normalizes_to_infinite_and_completes()
    {
        // A duration past the CancelAfter limit must normalize to infinite (no timer scheduled);
        // if CancelAfter were called with it, it would throw ArgumentOutOfRangeException.
        var huge = TimeSpan.FromMilliseconds((double)(uint.MaxValue - 1) + 1);
        var manager = new McpClientManager([], huge);
        var client = new FakeMcpClient("srv") { Tools = [new McpToolInfo("echo", "d", "{}", true)] };

        var result = await manager.ConnectClientAsync(client, default);

        Assert.True(result.Connected);
        Assert.Equal(1, result.ToolCount);
    }

    [Fact]
    public async Task Prebuilt_constructor_default_timeout_is_infinite()
    {
        // The prebuilt/test constructor imposes no connect timeout by default: a client that blocks
        // on the token keeps running (no short deadline fires); only caller cancellation unwinds it.
        var manager = new McpClientManager([]);
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeMcpClient("srv")
        {
            InitOverride = async ct =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return [];
            },
        };

        var task = manager.ConnectClientAsync(client, caller.Token);
        await started.Task;

        var winner = await Task.WhenAny(task, Task.Delay(150));
        Assert.NotSame(task, winner); // still connecting: no manager timeout fired

        await caller.CancelAsync();
        var result = await task;
        Assert.False(result.Connected);
        Assert.Contains("was canceled", result.Error!);
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

        public string? ThrowOnDispose { get; init; }

        public string? ThrowOnPromptList { get; init; }

        /// <summary>When set, drives initialize so tests can script cancellation/timeout/typed failures.</summary>
        public Func<CancellationToken, Task<IReadOnlyList<McpToolInfo>>>? InitOverride { get; init; }

        public int DisposeCount { get; private set; }

        public bool Disposed => this.DisposeCount > 0;

        public Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken ct = default)
        {
            if (this.InitOverride is { } init)
            {
                return init(ct);
            }

            return this.ThrowOnInit is { } msg ? throw new McpException(msg) : Task.FromResult(this.Tools);
        }

        public Task<(string Text, bool IsError)> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
            => Task.FromResult((string.Empty, false));

        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);

        public Task<string> ReadResourceAsync(string uri, CancellationToken ct = default) => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct = default) =>
            this.ThrowOnPromptList is { } message
                ? throw new McpException(message)
                : Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);

        public Task<string> GetPromptAsync(string name, JsonNode? arguments, CancellationToken ct = default) => Task.FromResult(string.Empty);

        public ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            return this.ThrowOnDispose is { } message
                ? ValueTask.FromException(new McpException(message))
                : ValueTask.CompletedTask;
        }
    }
}
