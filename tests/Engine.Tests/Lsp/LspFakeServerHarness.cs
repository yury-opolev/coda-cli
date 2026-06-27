using System.Text.Json.Nodes;
using Coda.Agent.Lsp;
using Coda.JsonRpc;

namespace Engine.Tests.Lsp;

/// <summary>
/// Shared in-memory fake LSP server harness for LSP tests. Drives the server side of a
/// <see cref="DuplexStreamPair"/> so no real language server is spawned: answers
/// initialize/shutdown, tracks file-sync notifications, and emits publishDiagnostics on didSave.
/// </summary>
internal sealed class LspFakeServerLoop : IAsyncDisposable
{
    private readonly LspFakeTransport transport;
    private readonly CancellationTokenSource cts;
    private readonly Task loopTask;

    public int DidOpenCount;
    public int DidChangeCount;
    public int DidSaveCount;
    public int DidCloseCount;
    public int ShutdownCount;

    public readonly TaskCompletionSource<JsonNode?> DidSaveReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LspFakeServerLoop(LspFakeTransport transport)
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
                    await this.RespondAsync(msg, new JsonObject { ["capabilities"] = new JsonObject() }).ConfigureAwait(false);
                    continue;
                }

                if (method == "initialized")
                {
                    continue;
                }

                if (method == "shutdown" && hasId)
                {
                    Interlocked.Increment(ref this.ShutdownCount);
                    await this.RespondAsync(msg, null).ConfigureAwait(false);
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
                    await this.EmitDiagnosticsAsync().ConfigureAwait(false);
                    continue;
                }

                if (method == "textDocument/didClose")
                {
                    Interlocked.Increment(ref this.DidCloseCount);
                    continue;
                }

                if (hasId)
                {
                    await this.RespondAsync(msg, new JsonObject { ["echo"] = method }).ConfigureAwait(false);
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

    private Task RespondAsync(JsonNode request, JsonNode? result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]!.DeepClone(),
            ["result"] = result,
        };
        return JsonRpcMessageCodec.WriteMessageAsync(this.transport.ServerWrites, response, this.cts.Token);
    }

    private Task EmitDiagnosticsAsync()
    {
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
                            ["end"] = new JsonObject { ["line"] = 0, ["character"] = 5 },
                        },
                    },
                },
            },
        };
        return JsonRpcMessageCodec.WriteMessageAsync(this.transport.ServerWrites, diag, this.cts.Token);
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

/// <summary>An <see cref="ILspTransport"/> backed by a <see cref="DuplexStreamPair"/>.</summary>
internal sealed class LspFakeTransport : ILspTransport
{
    private readonly DuplexStreamPair pair;

    public LspFakeTransport(DuplexStreamPair pair)
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

/// <summary>Builds an initialized <see cref="LspServerManager"/> wired to a fake server.</summary>
internal static class LspFakeServerHarness
{
    public static LspServerConfig MakeConfig(string ext = ".ts", string lang = "typescript", int? startupTimeoutMs = 5000)
    {
        return new LspServerConfig(
            Command: "fake-lsp",
            Args: [],
            ExtensionToLanguage: new Dictionary<string, string> { [ext] = lang },
            Env: null,
            InitializationOptions: null,
            StartupTimeoutMs: startupTimeoutMs);
    }

    public static (LspServerManager Manager, LspFakeServerLoop Loop) BuildManager(
        string serverName = "ts-server",
        string ext = ".ts",
        string lang = "typescript")
    {
        var config = MakeConfig(ext, lang);
        var configs = new Dictionary<string, LspServerConfig> { [serverName] = config };

        LspFakeServerLoop? capturedLoop = null;

        LspServerInstance Factory(string name, LspServerConfig cfg)
        {
            var pair = new DuplexStreamPair();
            var transport = new LspFakeTransport(pair);
            capturedLoop = new LspFakeServerLoop(transport);
            var client = new LspClient(name, _ => Task.FromResult<ILspTransport>(transport));
            return new LspServerInstance(name, cfg, client);
        }

        var manager = new LspServerManager(configs, Factory);
        manager.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        return (manager, capturedLoop!);
    }
}
