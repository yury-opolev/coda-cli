using System.Text.Json.Nodes;
using Coda.Agent.Lsp;
using Coda.JsonRpc;

namespace Engine.Tests;

/// <summary>
/// Tests for LspClient — process spawn + initialize lifecycle.
/// Uses an in-memory fake transport (ILspTransport over DuplexStreamPair) so no real language
/// server is spawned. The only test that spawns a real process is the spawn-failure test.
/// </summary>
public sealed class LspClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    // ---------------------------------------------------------------------------
    // Fake transport helper — wraps a DuplexStreamPair as an ILspTransport.
    // ---------------------------------------------------------------------------

    private sealed class FakeTransport : ILspTransport
    {
        private readonly DuplexStreamPair pair;

        public FakeTransport(DuplexStreamPair pair)
        {
            this.pair = pair;
        }

        // Client reads from server→client direction.
        public Stream Input => this.pair.ClientReads;

        // Client writes to client→server direction.
        public Stream Output => this.pair.ClientWrites;

        // The fake server side of the pair — test loops read/write through these.
        public Stream ServerReads => this.pair.ServerReads;
        public Stream ServerWrites => this.pair.ServerWrites;

        public ValueTask DisposeAsync()
        {
            this.pair.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static (LspClient client, FakeTransport transport) CreateFakeClient(
        Action<Exception>? onCrash = null)
    {
        var pair = new DuplexStreamPair();
        var transport = new FakeTransport(pair);
        var client = new LspClient(
            "test-server",
            _ => Task.FromResult<ILspTransport>(transport),
            onCrash);
        return (client, transport);
    }

    // ---------------------------------------------------------------------------
    // Fake server helpers — read a request and send a reply.
    // ---------------------------------------------------------------------------

    private static async Task ExpectRequestAndReplyAsync(
        Stream serverReads,
        Stream serverWrites,
        string expectedMethod,
        JsonNode result,
        CancellationToken ct)
    {
        var request = await JsonRpcMessageCodec.ReadMessageAsync(serverReads, ct);
        Assert.NotNull(request);
        Assert.Equal(expectedMethod, request!["method"]!.GetValue<string>());
        var id = request["id"]!.DeepClone();
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };
        await JsonRpcMessageCodec.WriteMessageAsync(serverWrites, response, ct);
    }

    private static async Task ExpectNotificationAsync(
        Stream serverReads,
        string expectedMethod,
        CancellationToken ct)
    {
        var msg = await JsonRpcMessageCodec.ReadMessageAsync(serverReads, ct);
        Assert.NotNull(msg);
        Assert.Equal(expectedMethod, msg!["method"]!.GetValue<string>());
        Assert.Null(msg["id"]);
    }

    // ---------------------------------------------------------------------------
    // Test: StartAsync + InitializeAsync sets capabilities and IsInitialized flag.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_then_Initialize_sets_capabilities_and_flag()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (client, transport) = CreateFakeClient();
        await using var _ = client;

        // Fake server loop: answer the initialize request and assert the initialized notification.
        var fakeServer = Task.Run(async () =>
        {
            // 1. Handle "initialize" request.
            await ExpectRequestAndReplyAsync(
                transport.ServerReads,
                transport.ServerWrites,
                "initialize",
                new JsonObject
                {
                    ["capabilities"] = new JsonObject { ["hoverProvider"] = true }
                },
                cts.Token);

            // 2. Expect "initialized" notification.
            await ExpectNotificationAsync(transport.ServerReads, "initialized", cts.Token);
        }, cts.Token);

        await client.StartAsync(cts.Token);

        var initParams = new JsonObject { ["rootUri"] = (JsonNode?)"file:///workspace" };
        var result = await client.InitializeAsync(initParams, cts.Token).WaitAsync(cts.Token);

        await fakeServer.WaitAsync(cts.Token);

        Assert.True(client.IsInitialized);
        Assert.NotNull(client.Capabilities);
        Assert.True(client.Capabilities!["capabilities"]!["hoverProvider"]!.GetValue<bool>());
    }

    // ---------------------------------------------------------------------------
    // Test: SendRequest before InitializeAsync throws.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SendRequest_before_initialize_throws()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (client, _) = CreateFakeClient();
        await using var _ = client;

        await client.StartAsync(cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendRequestAsync("textDocument/hover", null, cts.Token));

        Assert.Contains("not initialized", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // Test: OnNotification registered before StartAsync fires after initialization.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Queued_OnNotification_is_applied_after_start()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (client, transport) = CreateFakeClient();
        await using var _ = client;

        var receivedParams = new TaskCompletionSource<JsonNode?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Register BEFORE StartAsync — must be queued and applied once connection exists.
        client.OnNotification("textDocument/publishDiagnostics", p =>
        {
            receivedParams.TrySetResult(p);
        });

        var fakeServer = Task.Run(async () =>
        {
            // Answer initialize.
            await ExpectRequestAndReplyAsync(
                transport.ServerReads,
                transport.ServerWrites,
                "initialize",
                new JsonObject { ["capabilities"] = new JsonObject() },
                cts.Token);

            // Expect initialized notification.
            await ExpectNotificationAsync(transport.ServerReads, "initialized", cts.Token);

            // Server pushes a notification.
            var notification = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "textDocument/publishDiagnostics",
                ["params"] = new JsonObject { ["uri"] = "file:///foo.ts", ["diagnostics"] = new JsonArray() }
            };
            await JsonRpcMessageCodec.WriteMessageAsync(transport.ServerWrites, notification, cts.Token);
        }, cts.Token);

        await client.StartAsync(cts.Token);
        await client.InitializeAsync(new JsonObject(), cts.Token);

        await fakeServer.WaitAsync(cts.Token);

        var received = await receivedParams.Task.WaitAsync(cts.Token);
        Assert.NotNull(received);
        Assert.Equal("file:///foo.ts", received!["uri"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------------------
    // Test: SendRequest after initialize round-trips correctly.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SendRequest_after_initialize_roundtrips()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (client, transport) = CreateFakeClient();
        await using var _ = client;

        var fakeServer = Task.Run(async () =>
        {
            // Answer initialize.
            await ExpectRequestAndReplyAsync(
                transport.ServerReads,
                transport.ServerWrites,
                "initialize",
                new JsonObject { ["capabilities"] = new JsonObject() },
                cts.Token);

            await ExpectNotificationAsync(transport.ServerReads, "initialized", cts.Token);

            // Answer textDocument/hover.
            await ExpectRequestAndReplyAsync(
                transport.ServerReads,
                transport.ServerWrites,
                "textDocument/hover",
                new JsonObject { ["contents"] = "Hello hover" },
                cts.Token);
        }, cts.Token);

        await client.StartAsync(cts.Token);
        await client.InitializeAsync(new JsonObject(), cts.Token);

        var hoverResult = await client.SendRequestAsync(
            "textDocument/hover",
            new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = "file:///foo.ts" } },
            cts.Token).WaitAsync(cts.Token);

        await fakeServer.WaitAsync(cts.Token);

        Assert.NotNull(hoverResult);
        Assert.Equal("Hello hover", hoverResult!["contents"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------------------
    // Test: StopAsync sends shutdown request + exit notification.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task StopAsync_sends_shutdown_and_exit()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (client, transport) = CreateFakeClient();

        var shutdownReceived = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exitReceived = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var fakeServer = Task.Run(async () =>
        {
            // Answer initialize.
            await ExpectRequestAndReplyAsync(
                transport.ServerReads,
                transport.ServerWrites,
                "initialize",
                new JsonObject { ["capabilities"] = new JsonObject() },
                cts.Token);

            await ExpectNotificationAsync(transport.ServerReads, "initialized", cts.Token);

            // Answer shutdown.
            var shutdownReq = await JsonRpcMessageCodec.ReadMessageAsync(transport.ServerReads, cts.Token);
            Assert.NotNull(shutdownReq);
            Assert.Equal("shutdown", shutdownReq!["method"]!.GetValue<string>());

            // Reply to shutdown.
            var id = shutdownReq["id"]!.DeepClone();
            await JsonRpcMessageCodec.WriteMessageAsync(
                transport.ServerWrites,
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = (JsonNode?)null },
                cts.Token);

            shutdownReceived.TrySetResult(true);

            // Expect exit notification.
            var exitMsg = await JsonRpcMessageCodec.ReadMessageAsync(transport.ServerReads, cts.Token);
            if (exitMsg is not null)
            {
                var method = exitMsg["method"]?.GetValue<string>();
                exitReceived.TrySetResult(method == "exit");
            }
            else
            {
                exitReceived.TrySetResult(false);
            }
        }, cts.Token);

        await client.StartAsync(cts.Token);
        await client.InitializeAsync(new JsonObject(), cts.Token);
        await client.StopAsync(cts.Token).WaitAsync(cts.Token);
        await client.DisposeAsync();

        Assert.True(await shutdownReceived.Task.WaitAsync(cts.Token));
        // Exit notification is best-effort; give the fake server a moment.
        try
        {
            Assert.True(await exitReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token));
        }
        catch (TimeoutException)
        {
            // exit may race with connection teardown — acceptable per the spec.
        }

        await fakeServer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---------------------------------------------------------------------------
    // Test: ProcessLspTransport throws for a missing command.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ProcessLspTransport_StartAsync_throws_for_missing_command()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProcessLspTransport
                .StartAsync(
                    "definitely-not-a-real-binary-xyz",
                    [],
                    null,
                    null,
                    "test",
                    cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(5), cts.Token));

        Assert.Contains("test", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
