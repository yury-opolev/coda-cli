using Coda.Agent;

namespace Coda.Mcp;

/// <summary>
/// Connects all configured stdio MCP servers, aggregates their tools (as
/// <see cref="ITool"/>s), and owns the server processes. A failing or slow server
/// is skipped (logged) rather than blocking startup.
/// </summary>
public sealed class McpClientManager : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly List<McpStdioClient> clients = [];
    private readonly List<ITool> tools = [];
    private readonly bool ownsClients;

    /// <summary>Standard constructor: starts with no clients (use <see cref="ConnectAllAsync"/> to populate).</summary>
    public McpClientManager()
    {
        this.ownsClients = true;
    }

    /// <summary>
    /// Test-only constructor: accepts pre-built clients so tests can inject
    /// scripted connections without launching real processes.
    /// </summary>
    internal McpClientManager(IEnumerable<McpStdioClient> prebuiltClients)
    {
        this.ownsClients = false;
        this.clients.AddRange(prebuiltClients);
    }

    public IReadOnlyList<ITool> Tools => this.tools;

    /// <summary>Exposes the connected clients for resource/prompt fan-out operations.</summary>
    public IReadOnlyList<McpStdioClient> Clients => this.clients;

    /// <summary>Connect every server in <paramref name="servers"/>; <paramref name="log"/> receives status/errors.</summary>
    public async Task ConnectAllAsync(
        IReadOnlyDictionary<string, McpServerConfig> servers,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var (name, config) in servers)
        {
            McpStdioClient? client = null;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(ConnectTimeout);

                client = new McpStdioClient(name, config);
                var serverTools = await client.InitializeAndListToolsAsync(timeoutCts.Token).ConfigureAwait(false);

                this.clients.Add(client);
                foreach (var toolInfo in serverTools)
                {
                    this.tools.Add(new McpTool(client, name, toolInfo));
                }

                log?.Invoke($"MCP server '{name}': {serverTools.Count} tool(s).");
            }
            catch (Exception ex)
            {
                log?.Invoke($"MCP server '{name}' failed to connect: {ex.Message}");
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Fan out <c>resources/list</c> to all connected clients and aggregate the results.
    /// Per-client errors are swallowed so a single misbehaving server does not block the others.
    /// </summary>
    public async Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken cancellationToken = default)
    {
        var tasks = this.clients
            .Select(c => this.TryListResourcesAsync(c, cancellationToken))
            .ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(r => r).ToList();
    }

    /// <summary>
    /// Read a resource from the named server.
    /// Returns an informational message if no client with that server name is connected.
    /// </summary>
    public async Task<string> ReadResourceAsync(string serverName, string uri, CancellationToken cancellationToken = default)
    {
        var client = this.clients.FirstOrDefault(c => c.ServerName == serverName);
        if (client is null)
        {
            return $"No MCP server named '{serverName}' is connected.";
        }

        return await client.ReadResourceAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fan out <c>prompts/list</c> to all connected clients and aggregate the results.
    /// Per-client errors are swallowed so a single misbehaving server does not block the others.
    /// </summary>
    public async Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken cancellationToken = default)
    {
        var tasks = this.clients
            .Select(c => this.TryListPromptsAsync(c, cancellationToken))
            .ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(r => r).ToList();
    }

    /// <summary>
    /// Get a rendered prompt from the named server.
    /// Returns an informational message if no client with that server name is connected.
    /// </summary>
    public async Task<string> GetPromptAsync(string serverName, string promptName, CancellationToken cancellationToken = default)
    {
        var client = this.clients.FirstOrDefault(c => c.ServerName == serverName);
        if (client is null)
        {
            return $"No MCP server named '{serverName}' is connected.";
        }

        return await client.GetPromptAsync(promptName, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<McpPromptInfo>> TryListPromptsAsync(McpStdioClient client, CancellationToken cancellationToken)
    {
        try
        {
            return await client.ListPromptsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<McpResourceInfo>> TryListResourcesAsync(McpStdioClient client, CancellationToken cancellationToken)
    {
        try
        {
            return await client.ListResourcesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.ownsClients)
        {
            foreach (var client in this.clients)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }

        this.clients.Clear();
        this.tools.Clear();
    }
}
