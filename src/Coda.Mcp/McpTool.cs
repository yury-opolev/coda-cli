using System.Text.Json;
using System.Text.RegularExpressions;
using Coda.Agent;

namespace Coda.Mcp;

/// <summary>
/// Bridges an MCP server tool to the agent's <see cref="ITool"/> abstraction. The
/// advertised name is <c>mcp__{server}__{tool}</c> (matching the reference client);
/// calls are forwarded to the server via an <see cref="IMcpClient"/>.
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

    private readonly IMcpClient client;
    private readonly McpToolInfo info;

    public McpTool(IMcpClient client, string serverName, McpToolInfo info)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.info = info ?? throw new ArgumentNullException(nameof(info));
        this.ServerName = serverName;
        this.Name = $"mcp__{Sanitize(serverName)}__{Sanitize(info.Name)}";
    }

    /// <summary>The configured MCP server this tool belongs to (unsanitized).</summary>
    public string ServerName { get; }

    public string Name { get; }

    public string Description => this.info.Description;

    public string InputSchemaJson => this.info.InputSchemaJson;

    public bool IsReadOnly => this.info.ReadOnly;

    public bool ShouldDefer => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var timeout = ResolveTimeout(Environment.GetEnvironmentVariable(TimeoutEnv));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(timeout);
        }

        try
        {
            var (text, isError) = await this.client.CallToolAsync(this.info.Name, input, timeoutCts.Token).ConfigureAwait(false);
            return new ToolResult(text, isError);
        }
        catch (McpException ex)
        {
            return new ToolResult($"MCP tool error: {ex.Message}", IsError: true);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The tool's own timeout fired (not a caller/turn cancel) — return a clean error to
            // the model instead of aborting the whole turn. The caller-cancel path is left to
            // propagate so an interrupt still unwinds the turn.
            return new ToolResult($"MCP tool '{this.info.Name}' timed out after {timeout.TotalSeconds:N0}s.", IsError: true);
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
}
