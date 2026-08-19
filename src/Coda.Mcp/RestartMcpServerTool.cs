using System.Text;
using System.Text.Json;
using Coda.Agent;

namespace Coda.Mcp;

/// <summary>
/// Agent tool that restarts a single MCP server: disconnect it (which kills a stdio server's whole
/// process tree) and connect it again. This is the recovery path for the common stdio failure mode
/// where a server stops responding but its process is still alive, so every tool call against it
/// hangs or fails until the process is replaced.
/// <para>
/// It runs autonomously (<see cref="IsReadOnly"/> is true, so no permission prompt): a restart is
/// plain lifecycle management of a process the session already launched, and the breakage it repairs
/// shows up mid-turn when nobody is watching, so prompting would defeat the point.
/// </para>
/// <para>
/// Two rules keep it doing exactly what its name says. The server must be present in the merged
/// <c>.mcp.json</c> configuration AND enabled there, so a name that is unknown or carries
/// <c>"disabled": true</c> is rejected without touching the runtime. And the relaunch uses the
/// configuration <see cref="McpClientManager"/> already connected that server with, never a freshly
/// read one — restarting means re-launching what this session is running, not applying an edit.
/// </para>
/// </summary>
public sealed class RestartMcpServerTool : ITool
{
    private static readonly string schema = """
        {
          "type": "object",
          "properties": {
            "server": {
              "type": "string",
              "description": "Name of the configured, enabled MCP server to restart, exactly as it appears in .mcp.json."
            }
          },
          "required": ["server"]
        }
        """;

    private readonly McpClientManager manager;
    private readonly IMcpServerConfigSource configSource;

    /// <summary>
    /// Makes the configuration check and the restart one step, so a concurrent call cannot act on a
    /// server that was still enabled when this call read the config. Never disposed: an
    /// <see cref="ITool"/> has no disposal contract, and a <see cref="SemaphoreSlim"/> only allocates
    /// a kernel handle if <c>AvailableWaitHandle</c> is touched, which it never is here.
    /// </summary>
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <param name="manager">The live runtime whose client for the server is replaced.</param>
    /// <param name="configSource">Supplies the configured servers, including disabled ones.</param>
    public RestartMcpServerTool(McpClientManager manager, IMcpServerConfigSource configSource)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        this.configSource = configSource ?? throw new ArgumentNullException(nameof(configSource));
    }

    public string Name => "restart_mcp_server";

    public string Description =>
        "Restart one MCP server that this session is running: stop it (terminating its process) and start " +
        "it again. Call this yourself, without asking, as soon as a server looks unresponsive — its tools " +
        "time out, hang, or stop returning results — then retry the failed call. Fails without side effects " +
        "when the server is not configured, is disabled, or was never started in this session. If restarting " +
        "the same server twice does not fix it, stop and tell the user instead of restarting again.";

    public string InputSchemaJson => schema;

    /// <summary>
    /// True so the model can self-heal a broken server mid-turn without a permission prompt.
    /// Restarting is not a privileged operation: it re-launches an already-running, already-approved
    /// process with its recorded configuration and touches nothing else.
    /// </summary>
    public bool IsReadOnly => true;

    public string? SearchHint => "restart, reconnect or recover a hung, stuck or unresponsive MCP server";

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("server", out var serverProp)
            || serverProp.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(serverProp.GetString()))
        {
            return new ToolResult("Missing required argument: server", IsError: true);
        }

        var serverName = serverProp.GetString()!.Trim();

        // Checked and acted on under one lock: a server that passes the enabled gate must not be
        // restarted after a concurrent call (or /mcp disable) has taken it away.
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await this.RestartAsync(serverName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    private async Task<ToolResult> RestartAsync(string serverName, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, McpServerConfig> servers;
        try
        {
            servers = this.configSource.LoadServers();
        }
        catch (Exception ex) when (ex is McpException or IOException or UnauthorizedAccessException)
        {
            return new ToolResult(
                $"Cannot read the MCP configuration: {McpClientManager.SanitizeRuntimeError(ex.Message)}",
                IsError: true);
        }

        var safeName = McpSchemaPolicyFilter.Safe(serverName);

        if (!servers.TryGetValue(serverName, out var config))
        {
            return new ToolResult(
                $"MCP server '{safeName}' is not configured.{DescribeAvailable(servers)}",
                IsError: true);
        }

        if (config.Disabled)
        {
            return new ToolResult(
                $"MCP server '{safeName}' is disabled in the MCP configuration and was not restarted. " +
                "Enable it first (/mcp enable) if it should be running.",
                IsError: true);
        }

        // Restarts what this session already launched — see the type remarks for why the on-disk
        // configuration gates the request but does not supply the command that gets run.
        var outcome = await this.manager.RestartServerAsync(serverName, cancellationToken).ConfigureAwait(false);
        if (outcome is not { } restart)
        {
            return new ToolResult(
                $"MCP server '{safeName}' has not been started in this session, so there is nothing to " +
                "restart. Start it with /mcp start.",
                IsError: true);
        }

        // ConnectClientAsync converts a cancelled connect into a failure result, which would otherwise
        // be reported as a genuine restart failure for a turn the user simply cancelled.
        cancellationToken.ThrowIfCancellationRequested();

        var (result, wasRunning) = restart;
        if (!result.Connected)
        {
            return new ToolResult(
                $"Failed to restart MCP server '{safeName}': " +
                $"{McpClientManager.SanitizeRuntimeError(result.Error ?? "unknown error")}. " +
                "It is now stopped, so its tools are unavailable.",
                IsError: true);
        }

        var message = new StringBuilder()
            .Append(wasRunning ? "Restarted" : "Started")
            .Append(" MCP server '")
            .Append(safeName)
            .Append("': ")
            .Append(result.ToolCount)
            .Append(result.ToolCount == 1 ? " tool" : " tools")
            .Append(" available.");

        if (!wasRunning)
        {
            message.Append(" It was not running before this call.");
        }

        if (result.SchemaWarning is { } warning)
        {
            message.Append(' ').Append(McpClientManager.SanitizeRuntimeError(warning));
        }

        return new ToolResult(message.ToString());
    }

    /// <summary>Name the enabled servers so a wrong name is self-correcting rather than a dead end.</summary>
    private static string DescribeAvailable(IReadOnlyDictionary<string, McpServerConfig> servers)
    {
        var enabled = servers
            .Where(pair => !pair.Value.Disabled)
            .Select(pair => McpSchemaPolicyFilter.Safe(pair.Key))
            .Order(StringComparer.Ordinal)
            .ToList();

        return enabled.Count == 0
            ? " No MCP servers are configured and enabled."
            : $" Configured and enabled servers: {string.Join(", ", enabled)}.";
    }
}
