using System.Text.Json.Nodes;
using Coda.JsonRpc;

namespace Coda.Agent.Lsp;

/// <summary>
/// Manages the LSP client lifecycle: transport creation, JSON-RPC connection,
/// and the initialize/initialized handshake.
/// </summary>
public sealed class LspClient : IAsyncDisposable
{
    private readonly string serverName;
    private readonly Func<CancellationToken, Task<ILspTransport>> transportFactory;
    private readonly Action<Exception>? onCrash;

    // Handlers registered before StartAsync must be queued and applied once the
    // connection exists (mirrors the reference's pendingHandlers queue).
    private readonly List<(string method, Action<JsonNode?> handler)> pendingNotificationHandlers = [];
    private readonly List<(string method, Func<JsonNode?, JsonNode?> handler)> pendingRequestHandlers = [];

    private ILspTransport? transport;
    private IJsonRpcConnection? connection;
    private volatile bool initialized;
    private JsonNode? capabilities;

    /// <summary>
    /// Creates a new LspClient.
    /// </summary>
    /// <param name="serverName">Human-readable server name, used in error messages.</param>
    /// <param name="transportFactory">
    ///     Factory invoked by <see cref="StartAsync"/> to obtain a started transport.
    ///     The production caller passes
    ///     <c>ct => ProcessLspTransport.StartAsync(command, args, env, cwd, serverName, ct)</c>.
    /// </param>
    /// <param name="onCrash">Optional callback invoked when the server crashes.</param>
    public LspClient(
        string serverName,
        Func<CancellationToken, Task<ILspTransport>> transportFactory,
        Action<Exception>? onCrash = null)
    {
        this.serverName = serverName;
        this.transportFactory = transportFactory;
        this.onCrash = onCrash;
    }

    /// <summary>
    /// The raw capabilities node returned by the server's initialize response,
    /// or null if not yet initialized.
    /// </summary>
    public JsonNode? Capabilities => this.capabilities;

    /// <summary>Whether the initialize handshake has completed successfully.</summary>
    public bool IsInitialized => this.initialized;

    /// <summary>
    /// Invokes the transport factory, builds a JsonRpcConnection over its streams,
    /// and applies any handlers that were registered before this call.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        this.transport = await this.transportFactory(ct).ConfigureAwait(false);
        var conn = new JsonRpcConnection(this.transport.Input, this.transport.Output);
        this.connection = conn;

        // Apply queued notification handlers.
        foreach (var (method, handler) in this.pendingNotificationHandlers)
        {
            conn.OnNotification(method, handler);
        }

        this.pendingNotificationHandlers.Clear();

        // Apply queued request handlers.
        foreach (var (method, handler) in this.pendingRequestHandlers)
        {
            conn.OnRequest(method, handler);
        }

        this.pendingRequestHandlers.Clear();
    }

    /// <summary>
    /// Sends the LSP initialize request, stores the returned capabilities,
    /// sends the initialized notification, and sets <see cref="IsInitialized"/>.
    /// </summary>
    public async Task<JsonNode> InitializeAsync(JsonNode initializeParams, CancellationToken ct)
    {
        if (this.connection is null)
        {
            throw new InvalidOperationException($"LSP server '{this.serverName}' not started.");
        }

        var result = await this.connection
            .SendRequestAsync("initialize", initializeParams, ct)
            .ConfigureAwait(false);

        if (result is null)
        {
            throw new InvalidOperationException(
                $"LSP server '{this.serverName}' returned null for initialize.");
        }

        this.capabilities = result;

        await this.connection
            .SendNotificationAsync("initialized", new System.Text.Json.Nodes.JsonObject(), ct)
            .ConfigureAwait(false);

        this.initialized = true;
        return result;
    }

    /// <summary>
    /// Sends an LSP request. Throws if <see cref="InitializeAsync"/> has not been called.
    /// </summary>
    public Task<JsonNode?> SendRequestAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        if (!this.initialized)
        {
            throw new InvalidOperationException("LSP server not initialized.");
        }

        if (this.connection is null)
        {
            throw new InvalidOperationException($"LSP server '{this.serverName}' not started.");
        }

        return this.connection.SendRequestAsync(method, @params, ct);
    }

    /// <summary>
    /// Sends an LSP notification. Allowed as soon as the connection is started.
    /// </summary>
    public Task SendNotificationAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        if (this.connection is null)
        {
            throw new InvalidOperationException($"LSP server '{this.serverName}' not started.");
        }

        return this.connection.SendNotificationAsync(method, @params, ct);
    }

    /// <summary>
    /// Registers a notification handler. If called before <see cref="StartAsync"/>,
    /// the handler is queued and applied once the connection exists.
    /// </summary>
    public void OnNotification(string method, Action<JsonNode?> handler)
    {
        if (this.connection is not null)
        {
            this.connection.OnNotification(method, handler);
        }
        else
        {
            this.pendingNotificationHandlers.Add((method, handler));
        }
    }

    /// <summary>
    /// Registers a request handler. If called before <see cref="StartAsync"/>,
    /// the handler is queued and applied once the connection exists.
    /// </summary>
    public void OnRequest(string method, Func<JsonNode?, JsonNode?> handler)
    {
        if (this.connection is not null)
        {
            this.connection.OnRequest(method, handler);
        }
        else
        {
            this.pendingRequestHandlers.Add((method, handler));
        }
    }

    /// <summary>
    /// Sends the LSP shutdown request and exit notification (best-effort),
    /// then disposes the connection and transport.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        // Capture and null out the connection before setting initialized=false so that
        // the shutdown/exit messages can still be sent, but new callers already see
        // the connection as gone.
        var conn = this.connection;
        this.initialized = false;
        this.connection = null;

        if (conn is not null)
        {
            // Bound the shutdown handshake: a dead, hung, or non-conforming server may
            // never answer the request, and we must not block disposal forever waiting.
            try
            {
                using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                shutdownCts.CancelAfter(TimeSpan.FromSeconds(2));
                await conn
                    .SendRequestAsync("shutdown", new System.Text.Json.Nodes.JsonObject(), shutdownCts.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Server may be dead or unresponsive — swallow and proceed to exit/dispose.
            }

            try
            {
                await conn
                    .SendNotificationAsync("exit", null, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort.
            }

            await conn.DisposeAsync().ConfigureAwait(false);

            if (this.transport is not null)
            {
                await this.transport.DisposeAsync().ConfigureAwait(false);
                this.transport = null;
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

}
