using System.Text.Json.Nodes;
using Coda.Agent.Lsp;
using Coda.JsonRpc;

namespace Engine.Tests.Lsp;

/// <summary>
/// Tests for LspServerInstance and LspServerManager using an in-memory fake transport
/// (DuplexStreamPair) so no real language server is spawned.
/// </summary>
public sealed class LspServerManagerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    // -------------------------------------------------------------------------
    // Fake transport helper
    // -------------------------------------------------------------------------

    private sealed class FakeTransport : ILspTransport
    {
        private readonly DuplexStreamPair pair;

        public FakeTransport(DuplexStreamPair pair)
        {
            this.pair = pair;
        }

        public Stream Input => this.pair.ClientReads;
        public Stream Output => this.pair.ClientWrites;
        public Stream ServerReads => this.pair.ServerReads;
        public Stream ServerWrites => this.pair.ServerWrites;

        public ValueTask DisposeAsync()
        {
            this.pair.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // -------------------------------------------------------------------------
    // FakeServerLoop — drives the server side of the duplex pair.
    // Answers initialize, tracks received notifications, can emit notifications.
    // -------------------------------------------------------------------------

    private sealed class FakeServerLoop : IAsyncDisposable
    {
        private readonly FakeTransport transport;
        private readonly CancellationTokenSource cts;
        private readonly Task loopTask;

        // Counts of specific notifications received from the client side.
        public int DidOpenCount;
        public int DidChangeCount;
        public int DidSaveCount;
        public int DidCloseCount;
        public int ShutdownCount;

        // Fires when didSave is received — tests can await this.
        public readonly TaskCompletionSource<JsonNode?> DidSaveReceived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Fires after initialize+initialized handshake completes.
        private readonly TaskCompletionSource<bool> initialized =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializedTask => this.initialized.Task;

        public FakeServerLoop(FakeTransport transport)
        {
            this.transport = transport;
            this.cts = new CancellationTokenSource();
            this.loopTask = Task.Run(this.RunAsync);
        }

        private async Task RunAsync()
        {
            try
            {
                while (!this.cts.IsCancellationRequested)
                {
                    var msg = await JsonRpcMessageCodec
                        .ReadMessageAsync(this.transport.ServerReads, this.cts.Token)
                        .ConfigureAwait(false);

                    if (msg is null)
                    {
                        return;
                    }

                    var method = msg["method"]?.GetValue<string>();
                    var hasId = msg["id"] is not null;

                    if (method == "initialize" && hasId)
                    {
                        var id = msg["id"]!.DeepClone();
                        var response = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = id,
                            ["result"] = new JsonObject
                            {
                                ["capabilities"] = new JsonObject()
                            }
                        };
                        await JsonRpcMessageCodec
                            .WriteMessageAsync(this.transport.ServerWrites, response, this.cts.Token)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (method == "initialized")
                    {
                        // Handshake complete.
                        this.initialized.TrySetResult(true);
                        continue;
                    }

                    if (method == "shutdown" && hasId)
                    {
                        Interlocked.Increment(ref this.ShutdownCount);
                        var id = msg["id"]!.DeepClone();
                        var response = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = id,
                            ["result"] = (JsonNode?)null
                        };
                        await JsonRpcMessageCodec
                            .WriteMessageAsync(this.transport.ServerWrites, response, this.cts.Token)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (method == "exit")
                    {
                        return;
                    }

                    if (method == "textDocument/didOpen")
                    {
                        Interlocked.Increment(ref this.DidOpenCount);
                        continue;
                    }

                    if (method == "textDocument/didChange")
                    {
                        Interlocked.Increment(ref this.DidChangeCount);
                        continue;
                    }

                    if (method == "textDocument/didSave")
                    {
                        Interlocked.Increment(ref this.DidSaveCount);
                        this.DidSaveReceived.TrySetResult(msg["params"]);

                        // Emit a publishDiagnostics notification after didSave.
                        var diag = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["method"] = "textDocument/publishDiagnostics",
                            ["params"] = new JsonObject
                            {
                                ["uri"] = "file:///test.ts",
                                ["diagnostics"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["message"] = "Test error",
                                        ["severity"] = 1,
                                        ["range"] = new JsonObject
                                        {
                                            ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                                            ["end"] = new JsonObject { ["line"] = 0, ["character"] = 5 }
                                        }
                                    }
                                }
                            }
                        };
                        await JsonRpcMessageCodec
                            .WriteMessageAsync(this.transport.ServerWrites, diag, this.cts.Token)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (method == "textDocument/didClose")
                    {
                        Interlocked.Increment(ref this.DidCloseCount);
                        continue;
                    }

                    // For any other request, send a simple echo result.
                    if (hasId)
                    {
                        var id = msg["id"]!.DeepClone();
                        var response = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = id,
                            ["result"] = new JsonObject { ["echo"] = method }
                        };
                        await JsonRpcMessageCodec
                            .WriteMessageAsync(this.transport.ServerWrites, response, this.cts.Token)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation.
            }
            catch
            {
                // Swallow — fake server terminating.
            }
        }

        /// <summary>Sends a notification FROM the fake server TO the client.</summary>
        public Task SendNotificationAsync(JsonNode notification, CancellationToken ct)
        {
            return JsonRpcMessageCodec.WriteMessageAsync(this.transport.ServerWrites, notification, ct);
        }

        public async ValueTask DisposeAsync()
        {
            await this.cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await this.loopTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // Bounded wait — swallow timeout/cancel.
            }

            this.cts.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // Factory helpers
    // -------------------------------------------------------------------------

    private static LspServerConfig MakeConfig(
        string ext = ".ts",
        string lang = "typescript",
        int? startupTimeoutMs = 5000)
    {
        return new LspServerConfig(
            Command: "fake-lsp",
            Args: [],
            ExtensionToLanguage: new Dictionary<string, string> { [ext] = lang },
            Env: null,
            InitializationOptions: null,
            StartupTimeoutMs: startupTimeoutMs);
    }

    private static (LspServerManager manager, FakeServerLoop loop) BuildManager(
        string serverName = "ts-server",
        string ext = ".ts",
        string lang = "typescript")
    {
        var config = MakeConfig(ext, lang);
        var configs = new Dictionary<string, LspServerConfig> { [serverName] = config };

        FakeServerLoop? capturedLoop = null;

        LspServerInstance Factory(string name, LspServerConfig cfg)
        {
            var pair = new DuplexStreamPair();
            var transport = new FakeTransport(pair);
            var loop = new FakeServerLoop(transport);
            capturedLoop = loop;

            var client = new LspClient(
                name,
                _ => Task.FromResult<ILspTransport>(transport));

            return new LspServerInstance(name, cfg, client);
        }

        var manager = new LspServerManager(configs, Factory);
        // Synchronously initialize so capturedLoop is set before caller uses it.
        manager.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

        return (manager, capturedLoop!);
    }

    // -------------------------------------------------------------------------
    // Test: Routes_request_to_server_by_extension
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Routes_request_to_server_by_extension()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (manager, loop) = BuildManager("ts-server", ".ts", "typescript");
        await using var _ = manager;
        await using var __ = loop;

        // .ts → ts-server; the fake server should echo the method.
        var result = await manager
            .SendRequestAsync("/workspace/file.ts", "textDocument/hover", null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        Assert.NotNull(result);
        Assert.Equal("textDocument/hover", result!["echo"]!.GetValue<string>());

        // .py → no server → null result.
        var pyResult = await manager
            .SendRequestAsync("/workspace/file.py", "textDocument/hover", null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        Assert.Null(pyResult);
    }

    // -------------------------------------------------------------------------
    // Test: EnsureServerStarted_starts_lazily_then_reuses
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EnsureServerStarted_starts_lazily_then_reuses()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        var config = MakeConfig();
        var configs = new Dictionary<string, LspServerConfig> { ["ts"] = config };

        var initializeCallCount = 0;
        FakeServerLoop? loop = null;

        LspServerInstance Factory(string name, LspServerConfig cfg)
        {
            var pair = new DuplexStreamPair();
            var transport = new FakeTransport(pair);
            loop = new FakeServerLoop(transport);

            var client = new LspClient(name, _ =>
            {
                Interlocked.Increment(ref initializeCallCount);
                return Task.FromResult<ILspTransport>(transport);
            });

            return new LspServerInstance(name, cfg, client);
        }

        var manager = new LspServerManager(configs, Factory);
        await manager.InitializeAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        await using var _ = manager;
        await using var __ = loop!;

        // Before first call: Stopped.
        var server = manager.GetServerForFile("/file.ts");
        Assert.NotNull(server);
        Assert.Equal(LspServerState.Stopped, server!.State);

        // First EnsureServerStarted: starts the server (calls the transport factory once).
        var started = await manager
            .EnsureServerStartedAsync("/file.ts", cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        Assert.NotNull(started);
        Assert.Equal(LspServerState.Running, started!.State);
        Assert.Equal(1, initializeCallCount);

        // Second call: already Running → no re-start.
        var second = await manager
            .EnsureServerStartedAsync("/file.ts", cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        Assert.Same(started, second);
        Assert.Equal(1, initializeCallCount);
    }

    // -------------------------------------------------------------------------
    // Test: OpenFile_sends_didOpen_once
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OpenFile_sends_didOpen_once()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (manager, loop) = BuildManager();
        await using var _ = manager;
        await using var __ = loop;

        // First open.
        await manager
            .OpenFileAsync("/workspace/test.ts", "const x = 1;", cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        // Second open of the same file — should be deduplicated.
        await manager
            .OpenFileAsync("/workspace/test.ts", "const x = 2;", cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        // Give the fake server a brief moment to process.
        await Task.Delay(100, cts.Token);

        Assert.Equal(1, loop.DidOpenCount);
        Assert.True(manager.IsFileOpen("/workspace/test.ts"));
    }

    // -------------------------------------------------------------------------
    // Test: SaveFile_triggers_publishDiagnostics
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveFile_triggers_publishDiagnostics()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (manager, loop) = BuildManager();
        await using var _ = manager;
        await using var __ = loop;

        // Register a diagnostics notification handler on the server instance.
        var diagReceived = new TaskCompletionSource<JsonNode?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var server = manager.GetServerForFile("/workspace/test.ts")!;
        server.OnNotification("textDocument/publishDiagnostics", p =>
        {
            diagReceived.TrySetResult(p);
        });

        // Open and save the file.
        await manager
            .OpenFileAsync("/workspace/test.ts", "const x = 1;", cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        // Wait until the server is running so SaveFileAsync won't be a no-op.
        await manager
            .EnsureServerStartedAsync("/workspace/test.ts", cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        await manager
            .SaveFileAsync("/workspace/test.ts", cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        // The fake server emits publishDiagnostics after didSave.
        var diag = await diagReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.NotNull(diag);
        Assert.NotNull(diag!["uri"]);
        Assert.Equal("file:///test.ts", diag["uri"]!.GetValue<string>());
    }

    // -------------------------------------------------------------------------
    // Test: Shutdown_stops_running_servers
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Shutdown_stops_running_servers()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (manager, loop) = BuildManager();
        await using var __ = loop;

        // Start the server by sending a request.
        await manager
            .EnsureServerStartedAsync("/workspace/file.ts", cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        var allBefore = manager.GetAllServers();
        Assert.NotEmpty(allBefore);
        Assert.Equal(LspServerState.Running, allBefore.Values.First().State);

        // Shutdown — fake server should receive shutdown/exit.
        await manager
            .ShutdownAsync(cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        // Manager cleared its internal state.
        Assert.Empty(manager.GetAllServers());

        // The fake server loop received at least the shutdown request.
        Assert.True(loop.ShutdownCount >= 1);
    }

    // -------------------------------------------------------------------------
    // Test: GetServerForFile_returns_null_for_unconfigured_extension
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetServerForFile_returns_null_for_unconfigured_extension()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var config = MakeConfig(".ts", "typescript");
        var configs = new Dictionary<string, LspServerConfig> { ["ts"] = config };
        var manager = new LspServerManager(configs, (name, cfg) =>
        {
            var pair = new DuplexStreamPair();
            var transport = new FakeTransport(pair);
            var client = new LspClient(name, _ => Task.FromResult<ILspTransport>(transport));
            return new LspServerInstance(name, cfg, client);
        });

        await manager.InitializeAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        var server = manager.GetServerForFile("/workspace/file.rb");
        Assert.Null(server);

        var serverTs = manager.GetServerForFile("/workspace/file.ts");
        Assert.NotNull(serverTs);
    }
}
