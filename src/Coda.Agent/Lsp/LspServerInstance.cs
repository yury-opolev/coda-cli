using System.Text.Json.Nodes;

namespace Coda.Agent.Lsp;

/// <summary>
/// Wraps an <see cref="LspClient"/> with configuration, a name, and lifecycle state.
/// Manages the startup/shutdown of a single LSP server.
/// </summary>
public sealed class LspServerInstance : IAsyncDisposable
{
    private readonly LspClient client;
    private readonly string? workspaceRoot;
    private volatile LspServerState state = LspServerState.Stopped;

    /// <summary>
    /// Creates a new <see cref="LspServerInstance"/>.
    /// </summary>
    /// <param name="name">Unique server name used in configuration and routing.</param>
    /// <param name="config">Server configuration (command, args, extensions, etc.).</param>
    /// <param name="client">Pre-constructed LSP client to drive.</param>
    /// <param name="workspaceRoot">
    ///     The project root used as the server's <c>rootUri</c>. When null, falls back to the
    ///     current process directory. Passing the session working directory makes the server
    ///     resolve project config (tsconfig, etc.) correctly even when Coda was launched elsewhere.
    /// </param>
    public LspServerInstance(string name, LspServerConfig config, LspClient client, string? workspaceRoot = null)
    {
        this.Name = name;
        this.Config = config;
        this.client = client;
        this.workspaceRoot = workspaceRoot;
    }

    /// <summary>Unique server identifier.</summary>
    public string Name { get; }

    /// <summary>Server configuration.</summary>
    public LspServerConfig Config { get; }

    /// <summary>Current lifecycle state; <see cref="LspServerState.Stopped"/> initially.</summary>
    public LspServerState State => this.state;

    /// <summary>
    /// Starts the server and performs the LSP initialize handshake.
    /// Transitions: Stopped/Error → Starting → Running.
    /// On timeout or any failure: → Error, then rethrows.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (this.state == LspServerState.Running || this.state == LspServerState.Starting)
        {
            return;
        }

        this.state = LspServerState.Starting;

        var timeoutMs = this.Config.StartupTimeoutMs ?? 15_000;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeoutMs);

        try
        {
            await this.client.StartAsync(linkedCts.Token).ConfigureAwait(false);

            var cwd = this.workspaceRoot ?? Directory.GetCurrentDirectory();
            string? rootUri;
            try
            {
                rootUri = new Uri(cwd).AbsoluteUri;
            }
            catch
            {
                rootUri = null;
            }

            var initParams = new JsonObject
            {
                ["processId"] = JsonValue.Create((int?)null),
                ["rootUri"] = rootUri is not null ? JsonValue.Create(rootUri) : null,
                ["capabilities"] = new JsonObject(),
                ["initializationOptions"] = this.Config.InitializationOptions?.DeepClone()
            };

            await this.client.InitializeAsync(initParams, linkedCts.Token).ConfigureAwait(false);

            this.state = LspServerState.Running;
        }
        catch (Exception)
        {
            this.state = LspServerState.Error;
            // Best-effort cleanup; ignore secondary exceptions.
            try
            {
                await this.client.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore.
            }

            throw;
        }
    }

    /// <summary>
    /// Sends an LSP request to the server.
    /// </summary>
    public Task<JsonNode?> SendRequestAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        return this.client.SendRequestAsync(method, @params, ct);
    }

    /// <summary>
    /// Sends an LSP notification to the server (fire-and-forget at the protocol level).
    /// </summary>
    public Task SendNotificationAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        return this.client.SendNotificationAsync(method, @params, ct);
    }

    /// <summary>
    /// Registers a handler for notifications pushed by the server.
    /// </summary>
    public void OnNotification(string method, Action<JsonNode?> handler)
    {
        this.client.OnNotification(method, handler);
    }

    /// <summary>
    /// Registers a handler for server-initiated requests (e.g. <c>workspace/configuration</c>).
    /// </summary>
    public void OnRequest(string method, Func<JsonNode?, JsonNode?> handler)
    {
        this.client.OnRequest(method, handler);
    }

    /// <summary>
    /// Sends the LSP shutdown/exit sequence and transitions state to Stopped.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await this.client.StopAsync(ct).ConfigureAwait(false);
        this.state = LspServerState.Stopped;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.client.DisposeAsync().ConfigureAwait(false);
        this.state = LspServerState.Stopped;
    }
}
