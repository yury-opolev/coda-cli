using System.Text.Json;
using System.Text.RegularExpressions;
using Coda.Agent;

namespace Coda.Mcp;

/// <summary>
/// Bridges an MCP server tool to the agent's <see cref="ITool"/> abstraction. The
/// advertised name is <c>mcp__{server}__{tool}</c> (matching the reference client);
/// calls are forwarded to whichever <see cref="IMcpClient"/> is serving that server at
/// the time of the call, resolved through <see cref="IMcpClientResolver"/> so the tool
/// survives a restart of its server.
/// </summary>
public sealed class McpTool : ITool
{
    /// <summary>
    /// Default MCP tool-call timeout: 10 minutes. An MCP call is otherwise unbounded (the
    /// server could hang), so once the orchestrator stops killing coda during tool execution
    /// (it now sees the tool-progress heartbeat) a hung call would hang the session forever.
    /// This bounds it at the operation layer: only the call fails, the session keeps running.
    /// Overridable via <see cref="TimeoutEnv"/>.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Environment variable overriding the MCP call timeout (whole seconds; &lt;= 0 disables).</summary>
    public const string TimeoutEnv = "CODA_MCP_TOOL_TIMEOUT";

    private readonly IMcpClientResolver clients;
    private readonly McpToolInfo info;

    /// <summary>
    /// Binds the tool to whichever client is serving <paramref name="serverName"/> at the moment of
    /// each call. Restarting the server replaces that client, and this wrapper follows it — which is
    /// what lets the model restart a hung server and immediately retry inside the same turn.
    /// </summary>
    public McpTool(IMcpClientResolver clients, string serverName, McpToolInfo info)
    {
        this.clients = clients ?? throw new ArgumentNullException(nameof(clients));
        this.info = info ?? throw new ArgumentNullException(nameof(info));
        this.ServerName = serverName;
        this.Name = $"mcp__{Sanitize(serverName)}__{Sanitize(info.Name)}";
    }

    /// <summary>
    /// Binds the tool to one fixed client. For callers that own a single connection outright; a tool
    /// built this way does not survive a restart of its server, so anything with a lifecycle should
    /// use the <see cref="IMcpClientResolver"/> overload.
    /// </summary>
    public McpTool(IMcpClient client, string serverName, McpToolInfo info)
        : this(new FixedClient(client ?? throw new ArgumentNullException(nameof(client))), serverName, info)
    {
    }

    /// <summary>The configured MCP server this tool belongs to (unsanitized).</summary>
    public string ServerName { get; }

    public string Name { get; }

    public string Description => this.info.Description;

    public string InputSchemaJson => this.info.InputSchemaJson;

    public bool IsReadOnly => this.info.ReadOnly;

    /// <summary>
    /// True when the server's advertised input schema was invalid and had to be repaired at
    /// ingestion; the tool may not accept its arguments correctly. Surfaced in <c>/mcp</c>.
    /// </summary>
    public bool SchemaCoerced => this.info.SchemaCoerced;

    public bool ShouldDefer => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        // Resolved per call, never captured: after a restart the previous client is disposed and its
        // process killed, so a captured one would fail (or hang) forever.
        if (this.clients.ClientFor(this.ServerName) is not { } client)
        {
            return new ToolResult(
                $"MCP server '{McpSchemaPolicyFilter.Safe(this.ServerName)}' is not connected, so '{this.info.Name}' " +
                "cannot run. Restart it with restart_mcp_server and try again.",
                IsError: true);
        }

        var timeout = ResolveTimeout(Environment.GetEnvironmentVariable(TimeoutEnv));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(timeout);
        }

        try
        {
            var (text, isError) = await client.CallToolAsync(this.info.Name, input, timeoutCts.Token).ConfigureAwait(false);
            return new ToolResult(text, isError);
        }
        catch (McpException ex)
        {
            return new ToolResult($"MCP tool error: {ex.Message}", IsError: true);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The transport died underneath the call (typically the server was restarted or stopped
            // while it was in flight). Report it as a tool failure the model can recover from rather
            // than letting a raw transport exception unwind the turn.
            return new ToolResult(
                $"MCP tool error: the connection to server '{McpSchemaPolicyFilter.Safe(this.ServerName)}' was lost. " +
                "Restart it with restart_mcp_server and try again.",
                IsError: true);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The tool's own timeout fired (not a caller/turn cancel) — return a clean error to
            // the model instead of aborting the whole turn. The caller-cancel path is left to
            // propagate so an interrupt still unwinds the turn.
            return new ToolResult(
                $"MCP tool '{this.info.Name}' timed out after {timeout.TotalSeconds:N0}s. " +
                "If the server is unresponsive, restart it with restart_mcp_server and try again.",
                IsError: true);
        }
    }

    /// <summary>
    /// Resolve the MCP call timeout from the raw <see cref="TimeoutEnv"/> value: whole seconds
    /// when parseable, <see cref="DefaultTimeout"/> when unset/unparseable, and
    /// <see cref="Timeout.InfiniteTimeSpan"/> (no timeout) when &lt;= 0.
    /// </summary>
    public static TimeSpan ResolveTimeout(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var seconds))
        {
            return DefaultTimeout;
        }

        return seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Tool names must match the model API charset (^[a-zA-Z0-9_-]+$).</summary>
    private static string Sanitize(string value) => Regex.Replace(value, "[^a-zA-Z0-9_-]", "_");

    /// <summary>Adapts a single owned client to the resolver seam; it is never replaced.</summary>
    private sealed class FixedClient(IMcpClient client) : IMcpClientResolver
    {
        public IMcpClient? ClientFor(string serverName) => client;
    }
}
