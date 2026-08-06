using Coda.Agent;
using Coda.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Coda.Mcp;

/// <summary>
/// Connects all configured MCP servers (stdio processes and HTTP endpoints), aggregates
/// their tools (as <see cref="ITool"/>s), and owns the stdio server processes. A failing or
/// slow server is skipped (logged) rather than blocking startup.
/// <para>
/// Thread-safety: <see cref="ConnectAllAsync"/> now runs all server connects concurrently and
/// MCP connect is started as a background task during interactive startup so submission is not
/// gated on it. All mutable state (<c>clients</c>, <c>tools</c>, error maps, <c>Version</c>) is
/// guarded by a single <c>gate</c> lock. Expensive I/O (initialize/tools-list) happens outside
/// the lock; only the final atomic adoption or the error-record write takes the lock.
/// Reader methods that need async work first snapshot the client list under the lock, then
/// perform the async call outside.
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

    /// <summary>Serialises all reads and writes of <c>clients</c>, <c>tools</c>, error maps, and <c>Version</c>.</summary>
    private readonly object gate = new();

    private readonly List<IMcpClient> clients = [];
    private readonly List<ITool> tools = [];
    private readonly Dictionary<string, string> lastConnectionErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeErrorSource> lastConnectionErrorSources = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-server description of invalid tool schemas found at the last connect, so <c>/mcp</c>
    /// can surface it long after the startup log line scrolled away.
    /// </summary>
    private readonly Dictionary<string, string> schemaWarnings = new(StringComparer.Ordinal);
    private readonly bool ownsClients;
    private readonly IMcpHttpClientFactory? httpFactory;

    /// <summary>
    /// The manager-owned connect (startup) timeout, already normalized so it is always safe to
    /// hand to <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>: a non-positive or
    /// over-the-limit duration is <see cref="Timeout.InfiniteTimeSpan"/> (no timer scheduled).
    /// </summary>
    private readonly TimeSpan connectTimeout;

    /// <summary>
    /// What to do about a server that advertises a tool schema the model APIs would reject.
    /// Repair always happens at ingestion; this decides whether such a tool is kept, dropped, or
    /// fatal to the whole server.
    /// </summary>
    private readonly McpSchemaPolicy schemaPolicy;

    /// <summary>
    /// Standard constructor: starts with no clients (use <see cref="ConnectAllAsync"/> to
    /// populate). <paramref name="httpFactory"/> builds clients for HTTP servers; when null,
    /// HTTP servers are skipped (logged). <paramref name="connectTimeout"/> overrides the connect
    /// timeout; when null it is resolved from <see cref="McpConnectTimeout.FromEnvironment"/>. Any
    /// override is normalized with <see cref="McpConnectTimeout.Normalize"/> exactly like an
    /// environment value.
    /// </summary>
    public McpClientManager(
        IMcpHttpClientFactory? httpFactory = null,
        TimeSpan? connectTimeout = null,
        McpSchemaPolicy schemaPolicy = McpSchemaPolicy.Coerce)
    {
        this.ownsClients = true;
        this.httpFactory = httpFactory;
        this.schemaPolicy = schemaPolicy;
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
    internal McpClientManager(
        IEnumerable<IMcpClient> prebuiltClients,
        TimeSpan? connectTimeout = null,
        McpSchemaPolicy schemaPolicy = McpSchemaPolicy.Coerce)
    {
        this.ownsClients = false;
        this.clients.AddRange(prebuiltClients);
        this.schemaPolicy = schemaPolicy;
        this.connectTimeout = connectTimeout is { } value
            ? McpConnectTimeout.Normalize(value)
            : Timeout.InfiniteTimeSpan;
    }

    /// <summary>Returns a snapshot of the currently connected tools; safe to enumerate while connects run concurrently.</summary>
    public IReadOnlyList<ITool> Tools
    {
        get
        {
            lock (this.gate) { return [..this.tools]; }
        }
    }

    /// <summary>Exposes a snapshot of the connected clients for resource/prompt fan-out operations.</summary>
    public IReadOnlyList<IMcpClient> Clients
    {
        get
        {
            lock (this.gate) { return [..this.clients]; }
        }
    }

    /// <summary>True when a client for <paramref name="serverName"/> is currently connected.</summary>
    public bool IsServerConnected(string serverName)
    {
        lock (this.gate) { return this.clients.Any(c => string.Equals(c.ServerName, serverName, StringComparison.Ordinal)); }
    }

    /// <summary>The identity a connected server reported at initialize, or null when not connected / none.</summary>
    public McpServerInfo? ServerInfoFor(string serverName)
    {
        lock (this.gate) { return this.clients.FirstOrDefault(c => string.Equals(c.ServerName, serverName, StringComparison.Ordinal))?.ServerInfo; }
    }

    /// <summary>
    /// The last safe, actionable runtime error for <paramref name="serverName"/>, or null when the
    /// server has not failed since its last successful operation.
    /// </summary>
    public string? LastConnectionErrorFor(string serverName)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        lock (this.gate) { return this.lastConnectionErrors.GetValueOrDefault(serverName); }
    }

    /// <summary>The connected tools that belong to <paramref name="serverName"/> (empty when not connected).</summary>
    public IReadOnlyList<McpTool> ServerTools(string serverName)
    {
        lock (this.gate) { return McpServerTools.ForServer(this.tools, serverName); }
    }

    /// <summary>
    /// What the schema policy did about invalid tool schemas advertised by
    /// <paramref name="serverName"/> at its last connect, or null when every schema was usable.
    /// Needed in the UI because a <em>skipped</em> tool leaves no trace in the tool list at all.
    /// </summary>
    public string? SchemaWarningFor(string serverName)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        lock (this.gate) { return this.schemaWarnings.GetValueOrDefault(serverName); }
    }

    /// <summary>
    /// A versioned, immutable snapshot of the connected servers for the UI status view: each server's
    /// name, the identity it reported at initialize, and its tool count. Server list is copied and
    /// name-ordered; no <see cref="IMcpClient"/> instances are surfaced.
    /// </summary>
    public McpRuntimeSnapshot GetSnapshot()
    {
        lock (this.gate)
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
    }

    /// <summary>
    /// Bumped on every connect/disconnect. A live tool source can compare it to detect changes
    /// (the TUI re-reads <see cref="Tools"/> per turn, so it picks up changes without polling).
    /// </summary>
    public int Version
    {
        get
        {
            lock (this.gate) { return this.version; }
        }
    }

    private int version;

    /// <summary>
    /// Connect every server in <paramref name="servers"/> concurrently (all in parallel, so startup
    /// costs the slowest server rather than the sum). Each result is logged via <paramref name="log"/>
    /// in arrival order using the same wording as before.
    /// </summary>
    public async Task ConnectAllAsync(
        IReadOnlyDictionary<string, McpServerConfig> servers,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var tasks = servers.Select(async kvp =>
        {
            var result = await this.ConnectServerAsync(kvp.Key, kvp.Value, cancellationToken).ConfigureAwait(false);

            // The server name is an unvalidated .mcp.json object key, and several hosts route this
            // log straight to Console.Error — so it is scrubbed of terminal escapes and newlines
            // before it can reach a terminal.
            var name = McpSchemaPolicyFilter.Safe(kvp.Key);
            log?.Invoke(result.Connected
                ? $"MCP server '{name}': {result.ToolCount} tool(s)."
                : $"MCP server '{name}' failed to connect: {result.Error}");

            // A repaired schema is not a connect failure, but the user must still learn that a
            // server shipped one — silently running a half-broken tool is how the original
            // incident stayed mysterious.
            if (result.SchemaWarning is { } warning)
            {
                log?.Invoke(warning);
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
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
        lock (this.gate) { this.SetLastConnectionError(name, error); }
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
        string? schemaWarning;
        List<ITool> newTools;
        try
        {
            var advertised = await client.InitializeAndListToolsAsync(linkedCts.Token).ConfigureAwait(false);

            // Report on the full advertised set (so a skipped tool is still counted) with wording
            // that matches what the policy actually did, then apply it. Under Strict this throws
            // and is handled exactly like any other connect failure: the client is disposed and
            // nothing is adopted.
            schemaWarning = McpSchemaPolicyFilter.DescribeCoercions(client.ServerName, advertised, this.schemaPolicy);
            serverTools = McpSchemaPolicyFilter.Apply(advertised, this.schemaPolicy, client.ServerName);

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
            lock (this.gate) { this.SetLastConnectionError(client.ServerName, error); }
            return McpConnectResult.Failure(error);
        }

        // Atomic adoption: only after initialize and every wrapper succeeded.
        // Hold the lock only for the brief state mutation — not across any await.
        lock (this.gate)
        {
            this.clients.Add(client);
            this.tools.AddRange(newTools);
            this.ClearLastConnectionError(client.ServerName);
            this.SetSchemaWarning(client.ServerName, schemaWarning);
            this.version++;
        }
        return McpConnectResult.Success(serverTools.Count, schemaWarning);
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
        IMcpClient? client;
        lock (this.gate)
        {
            client = this.clients.FirstOrDefault(c => string.Equals(c.ServerName, name, StringComparison.Ordinal));
            if (client is null)
            {
                return false;
            }

            this.clients.Remove(client);
            this.tools.RemoveAll(t => t is McpTool mcpTool && string.Equals(mcpTool.ServerName, name, StringComparison.Ordinal));
            this.schemaWarnings.Remove(name);
            this.version++;
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
            lock (this.gate) { this.ClearLastConnectionError(name); }
        }
        catch (Exception ex)
        {
            lock (this.gate) { this.SetLastConnectionError(name, this.SanitizeRuntimeError(ex.Message)); }
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
        IReadOnlyList<IMcpClient> snapshot;
        lock (this.gate) { snapshot = [..this.clients]; }

        var tasks = snapshot
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
        IMcpClient? client;
        lock (this.gate) { client = this.clients.FirstOrDefault(c => c.ServerName == serverName); }
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
        IReadOnlyList<IMcpClient> snapshot;
        lock (this.gate) { snapshot = [..this.clients]; }

        var tasks = snapshot
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
        IMcpClient? client;
        lock (this.gate) { client = this.clients.FirstOrDefault(c => string.Equals(c.ServerName, serverName, StringComparison.Ordinal)); }
        if (client is null)
        {
            return [];
        }

        try
        {
            var prompts = await client.ListPromptsAsync(ct).ConfigureAwait(false);
            lock (this.gate) { this.ClearCapabilityError(serverName); }
            return prompts;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (this.gate) { this.SetLastConnectionError(serverName, this.SanitizeRuntimeError(ex.Message), RuntimeErrorSource.Capability); }
            return [];
        }
    }

    /// <summary>
    /// Get a rendered prompt from the named server.
    /// Returns an informational message if no client with that server name is connected.
    /// </summary>
    public async Task<string> GetPromptAsync(string serverName, string promptName, CancellationToken cancellationToken = default)
    {
        IMcpClient? client;
        lock (this.gate) { client = this.clients.FirstOrDefault(c => c.ServerName == serverName); }
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
        IMcpClient? client;
        lock (this.gate) { client = this.clients.FirstOrDefault(c => string.Equals(c.ServerName, serverName, StringComparison.Ordinal)); }
        if (client is null)
        {
            return [];
        }

        try
        {
            var resources = await client.ListResourcesAsync(ct).ConfigureAwait(false);
            lock (this.gate) { this.ClearCapabilityError(serverName); }
            return resources;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (this.gate) { this.SetLastConnectionError(serverName, this.SanitizeRuntimeError(ex.Message), RuntimeErrorSource.Capability); }
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

    /// <summary>Records (or clears) a server's schema warning. Caller must hold <c>gate</c>.</summary>
    private void SetSchemaWarning(string serverName, string? warning)
    {
        if (warning is null)
        {
            this.schemaWarnings.Remove(serverName);
        }
        else
        {
            this.schemaWarnings[serverName] = warning;
        }
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
    /// control sequences plus Unicode control and format characters.
    /// </summary>
    private string SanitizeRuntimeError(string error)
    {
        try
        {
            var safe = TerminalEscapePattern().Replace(error, string.Empty);
            safe = ObfuscatedSecretAssignmentPattern().Replace(safe, RedactObfuscatedSecretAssignment);
            safe = SanitizeSingleLine(safe);
            safe = SecretRedactor.Redact(safe);
            safe = SecretAssignmentPattern().Replace(safe, $"$1$2{SecretRedactor.Placeholder}");
            safe = UrlPattern().Replace(safe, "[redacted URL]");
            return TelemetryText.Truncate(safe);
        }
        catch (RegexMatchTimeoutException)
        {
            return "MCP operation failed.";
        }
    }

    private static string SanitizeSingleLine(string text)
    {
        var stripped = TerminalEscapePattern().Replace(text, string.Empty);
        var builder = new StringBuilder(stripped.Length);
        var pendingSpace = false;

        foreach (var rune in stripped.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (IsControlOrFormat(rune))
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private static bool IsControlOrFormat(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control or UnicodeCategory.Format;
    }

    private static string RedactObfuscatedSecretAssignment(Match match) =>
        IsSecretAssignmentKey(match.Groups[1].Value)
            ? $"{match.Groups[1].Value}{match.Groups[2].Value}{SecretRedactor.Placeholder}"
            : match.Value;

    private static bool IsSecretAssignmentKey(string key)
    {
        var normalized = new StringBuilder(key.Length);
        foreach (var rune in key.EnumerateRunes())
        {
            if (!IsControlOrFormat(rune))
            {
                normalized.Append(rune.ToString());
            }
        }

        var name = normalized.ToString().ToLowerInvariant();
        return name.Equals("authorization", StringComparison.Ordinal) ||
               name.Equals("proxy-authorization", StringComparison.Ordinal) ||
               name.Equals("x-api-key", StringComparison.Ordinal) ||
               name.Equals("cookie", StringComparison.Ordinal) ||
               name.Equals("set-cookie", StringComparison.Ordinal) ||
               name.Equals("token", StringComparison.Ordinal) ||
               name.Equals("secret", StringComparison.Ordinal) ||
               name.Equals("password", StringComparison.Ordinal) ||
               name.Equals("api_key", StringComparison.Ordinal) ||
               name.Equals("api-key", StringComparison.Ordinal) ||
               name.Equals("apikey", StringComparison.Ordinal) ||
               name.Contains("token", StringComparison.Ordinal) ||
               name.Contains("secret", StringComparison.Ordinal) ||
               name.Contains("password", StringComparison.Ordinal) ||
               name.Contains("api_key", StringComparison.Ordinal) ||
               name.Contains("api-key", StringComparison.Ordinal) ||
               name.Contains("apikey", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\x1B(?:[@-Z\\_]|\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B\x9C]*(?:\x07|\x1B\\|\x9C))|\x9B[0-?]*[ -/]*[@-~]|\x9D[^\x07\x9C]*(?:\x07|\x9C)", RegexOptions.Compiled | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex TerminalEscapePattern();

    [GeneratedRegex(@"(?x)
        \b(authorization|proxy-authorization|x-api-key|cookie|set-cookie|
        token|secret|password|api[_-]?key|apikey|
        [a-z_][a-z0-9_-]*(?:token|secret|password|api[_-]?key)[a-z0-9_-]*)
        (\s*(?:=|:)\s*)(?:Bearer\s+)?(?:""[^""]*""|'[^']*'|[^\s;,]+)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(@"\b([a-z_][a-z0-9_\-\p{Cc}\p{Cf}]*)(\s*(?:=|:)\s*)(?:Bearer\s+)?(?:""[^""]*""|'[^']*'|[^\s;,]+)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex ObfuscatedSecretAssignmentPattern();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex UrlPattern();

    private enum RuntimeErrorSource
    {
        Connection,
        Capability,
    }

    public async ValueTask DisposeAsync()
    {
        if (!this.ownsClients)
        {
            lock (this.gate)
            {
                this.clients.Clear();
                this.tools.Clear();
            }
            return;
        }

        IReadOnlyList<IMcpClient> toDispose;
        lock (this.gate)
        {
            toDispose = [..this.clients];
            this.clients.Clear();
            this.tools.Clear();
        }

        foreach (var client in toDispose)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
