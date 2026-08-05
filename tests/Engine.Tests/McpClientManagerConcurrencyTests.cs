using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// Verifies thread-safety and parallel-connect behaviour introduced in the async-startup
/// perf work.
/// <list type="bullet">
/// <item>
///   Concurrent <see cref="McpClientManager.ConnectClientAsync"/> calls from multiple threads
///   must not corrupt the internal state (clients / tools / version / error maps).
/// </item>
/// <item>
///   <see cref="McpClientManager.ConnectAllAsync"/> must connect all servers concurrently so
///   total elapsed time is close to the slowest server, not the sum of all servers.
/// </item>
/// </list>
/// </summary>
public sealed class McpClientManagerConcurrencyTests
{
    // ─── Thread-safety: concurrent connects + snapshot reads ────────────────

    [Fact]
    public async Task Concurrent_ConnectClientAsync_leaves_consistent_state_without_exception()
    {
        // Why: ConnectClientAsync mutates clients/tools/version under a single lock. If the lock
        // were absent, concurrent adoptions could corrupt the lists or miss a version bump.
        // This test hammers 20 parallel connects and asserts the final counts match exactly.
        const int serverCount = 20;
        var manager = new McpClientManager([], connectTimeout: null);

        var tasks = Enumerable.Range(0, serverCount).Select(async i =>
        {
            var client = new DelayFakeMcpClient($"srv-{i}")
            {
                Tools = [new McpToolInfo($"tool-{i}", "d", "{}", true)],
            };
            await manager.ConnectClientAsync(client, default).ConfigureAwait(false);
        });

        await Task.WhenAll(tasks);

        Assert.Equal(serverCount, manager.Clients.Count);
        Assert.Equal(serverCount, manager.Tools.Count);
        Assert.Equal(serverCount, manager.Version);
    }

    [Fact]
    public async Task GetSnapshot_and_Tools_concurrent_with_connects_never_throw()
    {
        // Why: GetSnapshot and Tools are called from the UI thread while a background connect
        // may still be running. Both must snapshot under the lock and return consistent views.
        const int serverCount = 30;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var manager = new McpClientManager([], connectTimeout: null);
        var readerExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Reader loop: hammer GetSnapshot and Tools while connects run concurrently.
        var readerTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var snapshot = manager.GetSnapshot();
                    var tools = manager.Tools;
                    Assert.True(snapshot.Servers.Count >= 0);
                    Assert.True(tools.Count >= 0);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    readerExceptions.Add(ex);
                }
                await Task.Yield();
            }
        });

        var connectTasks = Enumerable.Range(0, serverCount).Select(async i =>
        {
            await Task.Delay(i * 2, cts.Token).ConfigureAwait(false);
            var client = new DelayFakeMcpClient($"srv-{i}")
            {
                Tools = [new McpToolInfo($"tool-{i}", "d", "{}", true)],
            };
            await manager.ConnectClientAsync(client, cts.Token).ConfigureAwait(false);
        });

        await Task.WhenAll(connectTasks);
        await cts.CancelAsync();
        try { await readerTask; } catch (OperationCanceledException) { }

        Assert.Empty(readerExceptions);
        Assert.Equal(serverCount, manager.Clients.Count);
        Assert.Equal(serverCount, manager.Tools.Count);
    }

    // ─── Parallel ConnectAllAsync ────────────────────────────────────────────

    [Fact]
    public async Task ConnectAllAsync_elapsed_is_close_to_slowest_not_sum()
    {
        // Why: the old serial foreach would cost SUM(delays). With Task.WhenAll the cost is
        // MAX(delays). We allow 50 % slack above MAX to avoid flakiness on busy CI hosts.
        const int delayA = 80;
        const int delayB = 120;
        const int delayC = 100;
        const int maxMs = delayB;

        var fakeClients = new Dictionary<string, DelayFakeMcpClient>
        {
            ["a"] = new("a") { ConnectDelayMs = delayA, Tools = [new McpToolInfo("t", "d", "{}", true)] },
            ["b"] = new("b") { ConnectDelayMs = delayB, Tools = [new McpToolInfo("t", "d", "{}", true)] },
            ["c"] = new("c") { ConnectDelayMs = delayC, Tools = [new McpToolInfo("t", "d", "{}", true)] },
        };

        var factory = new DelegatingMcpHttpFactory(name => fakeClients[name]);
        var manager = new McpClientManager(factory, connectTimeout: Timeout.InfiniteTimeSpan);

        var servers = fakeClients.Keys.ToDictionary(
            name => name,
            _ => (McpServerConfig)new McpHttpServerConfig(new Uri("http://fake.test/mcp"), new Dictionary<string, string>(), new McpAuthConfig(McpAuthMode.None, null)));

        var logs = new System.Collections.Concurrent.ConcurrentBag<string>();
        var sw = Stopwatch.StartNew();
        await manager.ConnectAllAsync(servers, log: logs.Add, cancellationToken: default);
        sw.Stop();

        var slackMs = maxMs + maxMs / 2;
        Assert.True(
            sw.ElapsedMilliseconds < slackMs,
            $"Elapsed {sw.ElapsedMilliseconds}ms should be < {slackMs}ms (max={maxMs}ms, sum={delayA + delayB + delayC}ms)");

        Assert.Equal(3, manager.Clients.Count);
        Assert.Equal(3, logs.Count);
        Assert.All(logs, msg => Assert.Contains("tool(s)", msg));
    }

    [Fact]
    public async Task ConnectAllAsync_failing_server_does_not_prevent_others()
    {
        // Why: with Task.WhenAll a failing server must not short-circuit the others.
        var fakeClients = new Dictionary<string, DelayFakeMcpClient>
        {
            ["good-a"] = new("good-a") { Tools = [new McpToolInfo("t", "d", "{}", true)] },
            ["bad"]    = new("bad")    { ThrowOnInit = "boom" },
            ["good-b"] = new("good-b") { Tools = [new McpToolInfo("t", "d", "{}", true)] },
        };

        var factory = new DelegatingMcpHttpFactory(name => fakeClients[name]);
        var manager = new McpClientManager(factory, connectTimeout: Timeout.InfiniteTimeSpan);
        var logs = new System.Collections.Concurrent.ConcurrentBag<string>();

        var servers = fakeClients.Keys.ToDictionary(
            name => name,
            _ => (McpServerConfig)new McpHttpServerConfig(new Uri("http://fake.test/mcp"), new Dictionary<string, string>(), new McpAuthConfig(McpAuthMode.None, null)));

        await manager.ConnectAllAsync(servers, log: logs.Add, cancellationToken: default);

        Assert.True(manager.IsServerConnected("good-a"));
        Assert.True(manager.IsServerConnected("good-b"));
        Assert.False(manager.IsServerConnected("bad"));
        Assert.Equal(3, logs.Count);
        Assert.Contains(logs, m => m.Contains("good-a") && m.Contains("tool(s)"));
        Assert.Contains(logs, m => m.Contains("good-b") && m.Contains("tool(s)"));
        Assert.Contains(logs, m => m.Contains("bad") && m.Contains("failed to connect"));
    }

    [Fact]
    public async Task ConnectAllAsync_all_results_logged_regardless_of_arrival_order()
    {
        // Why: each server logs its result as it arrives; no result must be silently dropped.
        const int n = 10;
        var fakeClients = Enumerable.Range(0, n)
            .ToDictionary(
                i => $"srv-{i}",
                i => new DelayFakeMcpClient($"srv-{i}")
                {
                    ConnectDelayMs = (n - i) * 5,  // reverse order so last-started finishes first
                    Tools = [new McpToolInfo($"t{i}", "d", "{}", true)],
                });

        var factory = new DelegatingMcpHttpFactory(name => fakeClients[name]);
        var manager = new McpClientManager(factory, connectTimeout: Timeout.InfiniteTimeSpan);
        var logs = new System.Collections.Concurrent.ConcurrentBag<string>();

        var servers = fakeClients.Keys.ToDictionary(
            name => name,
            _ => (McpServerConfig)new McpHttpServerConfig(new Uri("http://fake.test/mcp"), new Dictionary<string, string>(), new McpAuthConfig(McpAuthMode.None, null)));

        await manager.ConnectAllAsync(servers, log: logs.Add, cancellationToken: default);

        Assert.Equal(n, logs.Count);
        for (var i = 0; i < n; i++)
        {
            var name = $"srv-{i}";
            Assert.Contains(logs, msg => msg.Contains(name));
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fake <see cref="IMcpClient"/> with a configurable connect latency for concurrency and
    /// timing tests.
    /// </summary>
    private sealed class DelayFakeMcpClient(string serverName) : IMcpClient
    {
        public string ServerName { get; } = serverName;

        public IReadOnlyList<McpToolInfo> Tools { get; init; } = [];

        public string? ThrowOnInit { get; init; }

        /// <summary>Simulated connect latency in milliseconds; 0 means near-instant.</summary>
        public int ConnectDelayMs { get; init; }

        public async Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken ct = default)
        {
            if (this.ConnectDelayMs > 0)
            {
                await Task.Delay(this.ConnectDelayMs, ct).ConfigureAwait(false);
            }

            if (this.ThrowOnInit is { } msg) { throw new McpException(msg); }

            return this.Tools;
        }

        public Task<(string Text, bool IsError)> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
            => Task.FromResult((string.Empty, false));

        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);

        public Task<string> ReadResourceAsync(string uri, CancellationToken ct = default) => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);

        public Task<string> GetPromptAsync(string name, JsonNode? arguments, CancellationToken ct = default) => Task.FromResult(string.Empty);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Fake <see cref="IMcpHttpClientFactory"/> that resolves clients via a name-keyed delegate,
    /// enabling <see cref="McpClientManager.ConnectAllAsync"/> to be tested with injected fakes
    /// while still exercising the real <c>CreateClient → ConnectClientAsync</c> path for HTTP configs.
    /// </summary>
    private sealed class DelegatingMcpHttpFactory(Func<string, IMcpClient> clientResolver) : IMcpHttpClientFactory
    {
        public IMcpClient Create(string serverName, McpHttpServerConfig config) => clientResolver(serverName);
    }
}
