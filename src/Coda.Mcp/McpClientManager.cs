using Coda.Agent;
using Coda.Common;
using System.Text;
using System.Text.RegularExpressions;

namespace Coda.Mcp;

/// <summary>
/// Connects all configured MCP servers (stdio processes and HTTP endpoints), aggregates
/// their tools (as <see cref="ITool"/>s), and owns the stdio server processes. A failing or
/// slow server is skipped (logged) rather than blocking startup.
/// <para>
/// Thread-safety by convention: the client/tool lists are mutated only from the REPL thread
/// between agent turns (via <c>/mcp start|stop</c>) and read by a turn that has already started
/// (which copies <see cref="Tools"/> into a fresh array). There is no background reconnect, so no
/// lock is used. A future background mutator would need synchronization here.
/// </para>
/// </summary>
public sealed partial class McpClientManager : IAsyncDisposable
{
    /// <summary>
    /// Phase attributed to a startup failure that cannot be pinned to a single JSON-RPC method
    /// (e.g. a generic client that surfaced an <see cref="OperationCanceledException"/> directly).
    /// The startup handshake is <c>initialize</c> then <c>tools/list</c>.
    /// </summary>
    private const string DefaultConnectPhase = "initialize/tools/list";

    private readonly List<IMcpClient> clients = [];
    private readonly List<ITool> tools = [];
    private readonly Dictionary<string, string> lastConnectionErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeErrorSource> lastConnectionErrorSources = new(StringComparer.Ordinal);
    private readonly bool ownsClients;
    private readonly IMcpHttpClientFactory? httpFactory;

    /// <summary>
    /// The manager-owned connect (startup) timeout, already normalized so it is always safe to
    /// hand to <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>: a non-positive or
    /// over-the-limit duration is <see cref="Timeout.InfiniteTimeSpan"/> (no timer scheduled).
    /// </summary>
    private readonly TimeSpan connectTimeout;

    /// <summary>
    /// Standard constructor: starts with no clients (use <see cref="ConnectAllAsync"/> to
    /// populate). <paramref name="httpFactory"/> builds clients for HTTP servers; when null,
    /// HTTP servers are skipped (logged). <paramref name="connectTimeout"/> overrides the connect
    /// timeout; when null it is resolved from <see cref="McpConnectTimeout.FromEnvironment"/>. Any
    /// override is normalized with <see cref="McpConnectTimeout.Normalize"/> exactly like an
    /// environment value.
    /// </summary>
    public McpClientManager(IMcpHttpClientFactory? httpFactory = null, TimeSpan? connectTimeout = null)
    {
        this.ownsClients = true;
        this.httpFactory = httpFactory;
        this.connectTimeout = connectTimeout is { } value
            ? McpConnectTimeout.Normalize(value)
            : McpConnectTimeout.FromEnvironment();
    }

    /// <summary>
    /// Test-only constructor: accepts pre-built clients so tests can inject
    /// scripted connections without launching real processes. The connect timeout defaults to
    /// <see cref="Timeout.InfiniteTimeSpan"/> (no timer) unless a test supplies an explicit
    /// <paramref name="connectTimeout"/>, which is normalized like any other value.
    /// </summary>
    internal McpClientManager(IEnumerable<IMcpClient> prebuiltClients, TimeSpan? connectTimeout = null)
    {
        this.ownsClients = false;
        this.clients.AddRange(prebuiltClients);
        this.connectTimeout = connectTimeout is { } value
            ? McpConnectTimeout.Normalize(value)
            : Timeout.InfiniteTimeSpan;
    }

    public IReadOnlyList<ITool> Tools => this.tools;

    /// <summary>Exposes the connected clients for resource/prompt fan-out operations.</summary>
    public IReadOnlyList<IMcpClient> Clients => this.clients;

    /// <summary>True when a client for <paramref name="serverName"/> is currently connected.</summary>
    public bool IsServerConnected(string serverName) =>
        this.clients.Any(c => string.Equals(c.ServerName, serverName, StringComparison.Ordinal));

    /// <summary>The identity a connected server reported at initialize, or null when not connected / none.</summary>
    public McpServerInfo? ServerInfoFor(string serverName) =>
        this.clients.FirstOrDefault(c => string.Equals(c.ServerName, serverName, StringComparison.Ordinal))?.ServerInfo;

    /// <summary>
    /// The last safe, actionable runtime error for <paramref name="serverName"/>, or null when the
    /// server has not failed since its last successful operation.
    /// </summary>
    public string? LastConnectionErrorFor(string serverName)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        return this.lastConnectionErrors.GetValueOrDefault(serverName);
    }

    /// <summary>The connected tools that belong to <paramref name="serverName"/> (empty when not connected).</summary>
    public IReadOnlyList<McpTool> ServerTools(string serverName) => McpServerTools.ForServer(this.tools, serverName);

    /// <summary>
    /// A versioned, immutable snapshot of the connected servers for the UI status view: each server's
    /// name, the identity it reported at initialize, and its tool count. Server list is copied and
    /// name-ordered; no <see cref="IMcpClient"/> instances are surfaced.
    /// </summary>
    public McpRuntimeSnapshot GetSnapshot()
    {
        var servers = this.clients
            .OrderBy(c => c.ServerName, StringComparer.Ordinal)
            .Select(c => new McpServerRuntimeSnapshot(
                c.ServerName,
                c.ServerInfo,
                McpServerTools.ForServer(this.tools, c.ServerName).Count))
            .ToList();

        return new McpRuntimeSnapshot(this.Version, servers);
    }

    /// <summary>
    /// Bumped on every connect/disconnect. A live tool source can compare it to detect changes
    /// (the TUI re-reads <see cref="Tools"/> per turn, so it picks up changes without polling).
    /// </summary>
    public int Version { get; private set; }

    /// <summary>Connect every server in <paramref name="servers"/>; <paramref name="log"/> receives status/errors.</summary>
    public async Task ConnectAllAsync(
        IReadOnlyDictionary<string, McpServerConfig> servers,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var (name, config) in servers)
        {
            var result = await this.ConnectServerAsync(name, config, cancellationToken).ConfigureAwait(false);
            log?.Invoke(result.Connected
                ? $"MCP server '{name}': {result.ToolCount} tool(s)."
                : $"MCP server '{name}' failed to connect: {result.Error}");
        }
    }

    /// <summary>
    /// Connect a single server (add its tools). Returns a failure result — never throws — when the
    /// server is already connected, its transport is unavailable, or initialize fails.
    /// </summary>
    public async Task<McpConnectResult> ConnectServerAsync(string name, McpServerConfig config, CancellationToken cancellationToken = default)
    {
        if (this.IsServerConnected(name))
        {
            return McpConnectResult.Failure($"'{name}' is already connected.");
        }

        var client = this.CreateClient(name, config);
        if (client is not null)
        {
            return await this.ConnectClientAsync(client, cancellationToken).ConfigureAwait(false);
        }

        const string error = "HTTP transport is not available.";
        this.SetLastConnectionError(name, error);
        return McpConnectResult.Failure(error);
    }

    /// <summary>Initialize a pre-built client and adopt its tools (a test seam + the shared connect core).</summary>
    internal async Task<McpConnectResult> ConnectClientAsync(IMcpClient client, CancellationToken cancellationToken)
    {
        // One linked source combines caller cancellation with the manager's own connect policy;
        // CancelAfter runs only for a finite, positive, normalized duration (infinite => no timer).
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (this.connectTimeout != Timeout.InfiniteTimeSpan)
        {
            linkedCts.CancelAfter(this.connectTimeout);
        }

        IReadOnlyList<McpToolInfo> serverTools;
        List<ITool> newTools;
        try
        {
            serverTools = await client.InitializeAndListToolsAsync(linkedCts.Token).ConfigureAwait(false);

            // Build every wrapper into a temporary list before touching manager state, so a wrapper
            // failure cannot leave a half-registered client or a stray tool behind.
            newTools = new List<ITool>(serverTools.Count);
            foreach (var toolInfo in serverTools)
            {
                newTools.Add(new McpTool(client, client.ServerName, toolInfo));
            }
        }
        catch (Exception ex)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // The failed client was never adopted; preserve the original connection failure.
            }

            var callerCanceled = cancellationToken.IsCancellationRequested;
            var timedOut = !callerCanceled && linkedCts.IsCancellationRequested;
            var error = this.SanitizeRuntimeError(this.ClassifyFailure(ex, client.ServerName, callerCanceled, timedOut));
            this.SetLastConnectionError(client.ServerName, error);
            return McpConnectResult.Failure(error);
        }

        // Atomic adoption: only after initialize and every wrapper succeeded.
        this.clients.Add(client);
        this.tools.AddRange(newTools);
        this.ClearLastConnectionError(client.ServerName);
        this.Version++;
        return McpConnectResult.Success(serverTools.Count);
    }

    /// <summary>
    /// Map a connect failure to a user-facing message following a fixed precedence: caller
    /// cancellation, then the manager-owned timeout, then an existing typed connection error, then
    /// an unclassified operation cancellation, and finally any other exception's original message.
    /// A typed <see cref="McpConnectionException.Phase"/> is preserved when reclassifying so a
    /// timeout that unwound a specific handshake step still names that step; otherwise
    /// <see cref="DefaultConnectPhase"/> is used. Raw <see cref="OperationCanceledException"/> text
    /// is never surfaced.
    /// </summary>
    private string ClassifyFailure(Exception ex, string serverName, bool callerCanceled, bool timedOut)
    {
        var phase = (ex as McpConnectionException)?.Phase ?? DefaultConnectPhase;

        if (callerCanceled)
        {
            return McpConnectionException.Canceled(serverName, phase).Message;
        }

        if (timedOut)
        {
            return McpConnectionException.Timeout(serverName, phase, this.connectTimeout).Message;
        }

        if (ex is McpConnectionException typed)
        {
            return typed.Message;
        }

        if (ex is OperationCanceledException)
        {
            return McpConnectionException.Canceled(serverName, phase).Message;
        }

        return ex.Message;
    }

    /// <summary>
    /// Disconnect a single server: dispose its client and drop its tools. Returns false when no
    /// server with that name is connected.
    /// </summary>
    public async Task<bool> DisconnectServerAsync(string name)
    {
        var client = this.clients.FirstOrDefault(c => string.Equals(c.ServerName, name, StringComparison.Ordinal));
        if (client is null)
        {
            return false;
        }

        this.clients.Remove(client);
        this.tools.RemoveAll(t => t is McpTool mcpTool && string.Equals(mcpTool.ServerName, name, StringComparison.Ordinal));
        this.Version++;
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
            this.ClearLastConnectionError(name);
        }
        catch (Exception ex)
        {
            this.SetLastConnectionError(name, this.SanitizeRuntimeError(ex.Message));
        }

        return true;
    }

    /// <summary>Construct the transport-appropriate client, or null when an HTTP server has no factory.</summary>
    private IMcpClient? CreateClient(string name, McpServerConfig config)
    {
        return config switch
        {
            McpStdioServerConfig stdio => new McpStdioClient(name, stdio),
            McpHttpServerConfig http => this.httpFactory?.Create(name, http),
            _ => null,
        };
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
    /// List prompts from exactly the connected server named <paramref name="serverName"/>.
    /// Returns an empty list when it is absent or cannot list prompts.
    /// </summary>
    public async Task<IReadOnlyList<McpPromptInfo>> ServerPromptsAsync(string serverName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        var client = this.clients.FirstOrDefault(c => string.Equals(c.ServerName, serverName, StringComparison.Ordinal));
        if (client is null)
        {
            return [];
        }

        try
        {
            var prompts = await client.ListPromptsAsync(ct).ConfigureAwait(false);
            this.ClearCapabilityError(serverName);
            return prompts;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.SetLastConnectionError(serverName, this.SanitizeRuntimeError(ex.Message), RuntimeErrorSource.Capability);
            return [];
        }
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

    /// <summary>
    /// List resources from exactly the connected server named <paramref name="serverName"/>.
    /// Returns an empty list when it is absent or cannot list resources.
    /// </summary>
    public async Task<IReadOnlyList<McpResourceInfo>> ServerResourcesAsync(string serverName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        var client = this.clients.FirstOrDefault(c => string.Equals(c.ServerName, serverName, StringComparison.Ordinal));
        if (client is null)
        {
            return [];
        }

        try
        {
            var resources = await client.ListResourcesAsync(ct).ConfigureAwait(false);
            this.ClearCapabilityError(serverName);
            return resources;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.SetLastConnectionError(serverName, this.SanitizeRuntimeError(ex.Message), RuntimeErrorSource.Capability);
            return [];
        }
    }

    private async Task<IReadOnlyList<McpPromptInfo>> TryListPromptsAsync(IMcpClient client, CancellationToken cancellationToken)
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

    private async Task<IReadOnlyList<McpResourceInfo>> TryListResourcesAsync(IMcpClient client, CancellationToken cancellationToken)
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

    private void SetLastConnectionError(string serverName, string error, RuntimeErrorSource source = RuntimeErrorSource.Connection)
    {
        this.lastConnectionErrors[serverName] = error;
        this.lastConnectionErrorSources[serverName] = source;
    }

    private void ClearLastConnectionError(string serverName)
    {
        this.lastConnectionErrors.Remove(serverName);
        this.lastConnectionErrorSources.Remove(serverName);
    }

    private void ClearCapabilityError(string serverName)
    {
        if (this.lastConnectionErrorSources.GetValueOrDefault(serverName) == RuntimeErrorSource.Capability)
        {
            this.ClearLastConnectionError(serverName);
        }
    }

    /// <summary>
    /// Creates a bounded, single-line user-visible error after redacting secrets and removing terminal
    /// control sequences, controls, and bidirectional formatting characters.
    /// </summary>
    private string SanitizeRuntimeError(string error)
    {
        var safe = TerminalEscapePattern().Replace(error, string.Empty);
        safe = ObfuscatedSecretAssignmentPattern().Replace(safe, RedactObfuscatedSecretAssignment);
        safe = SanitizeSingleLine(safe);
        safe = SecretRedactor.Redact(safe);
        safe = SecretAssignmentPattern().Replace(safe, $"$1$2{SecretRedactor.Placeholder}");
        safe = UrlPattern().Replace(safe, "[redacted URL]");
        return TelemetryText.Truncate(safe);
    }

    private static string SanitizeSingleLine(string text)
    {
        var stripped = TerminalEscapePattern().Replace(text, string.Empty);
        var builder = new StringBuilder(stripped.Length);
        var pendingSpace = false;

        foreach (var ch in stripped)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (char.IsControl(ch) || IsBidiFormattingControl(ch))
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool IsBidiFormattingControl(char ch) => ch is
        '\u061C' or
        '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or
        '\u2066' or '\u2067' or '\u2068' or '\u2069' or
        '\u200E' or '\u200F';

    private static string RedactObfuscatedSecretAssignment(Match match) =>
        IsSecretAssignmentKey(match.Groups[1].Value)
            ? $"{match.Groups[1].Value}{match.Groups[2].Value}{SecretRedactor.Placeholder}"
            : match.Value;

    private static bool IsSecretAssignmentKey(string key)
    {
        var normalized = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            if (!char.IsControl(ch) && !IsBidiFormattingControl(ch))
            {
                normalized.Append(ch);
            }
        }

        var name = normalized.ToString();
        return name.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("cookie", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("token", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("password", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("api-key", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("apikey", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("api-key", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("apikey", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\x1B(?:[@-Z\\_]|\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B\x9C]*(?:\x07|\x1B\\|\x9C))|\x9B[0-?]*[ -/]*[@-~]|\x9D[^\x07\x9C]*(?:\x07|\x9C)", RegexOptions.Compiled)]
    private static partial Regex TerminalEscapePattern();

    [GeneratedRegex(@"(?ix)
        \b(authorization|proxy-authorization|x-api-key|cookie|set-cookie|
        token|secret|password|api[_-]?key|apikey|
        [a-z_][a-z0-9_-]*(?:token|secret|password|api[_-]?key)[a-z0-9_-]*)
        (\s*(?:=|:)\s*)(?:Bearer\s+)?(?:""[^""]*""|'[^']*'|[^\s;,]+)")]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(@"\b([a-z_][a-z0-9_\-\x00-\x1F\x7F-\x9F\u061C\u200E\u200F\u202A-\u202E\u2066-\u2069]*)(\s*(?:=|:)\s*)(?:Bearer\s+)?(?:""[^""]*""|'[^']*'|[^\s;,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ObfuscatedSecretAssignmentPattern();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    private enum RuntimeErrorSource
    {
        Connection,
        Capability,
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
