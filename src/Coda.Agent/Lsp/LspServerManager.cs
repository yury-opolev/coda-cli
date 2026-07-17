using System.Text.Json.Nodes;

namespace Coda.Agent.Lsp;

/// <summary>
/// Routes LSP requests by file extension, manages server lifecycle, and keeps
/// open-file state synchronized across all servers.
/// </summary>
public sealed class LspServerManager : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, LspServerConfig> configs;
    private readonly Func<string, LspServerConfig, LspServerInstance> instanceFactory;

    // Built during InitializeAsync.
    private readonly Dictionary<string, LspServerInstance> servers = [];
    // ext (with dot, lowercased) → first server name that handles it.
    private readonly Dictionary<string, string> extensionMap = [];
    // URI (file URI string) → server name of the server that received didOpen.
    private readonly Dictionary<string, string> openedFiles = [];
    // URI → monotonically-increasing version counter for didChange.
    private readonly Dictionary<string, int> fileVersions = [];

    /// <summary>
    /// Creates a new <see cref="LspServerManager"/>.
    /// </summary>
    /// <param name="configs">Named server configs; keys match the server names.</param>
    /// <param name="instanceFactory">
    ///     Factory that produces an <see cref="LspServerInstance"/> for a given name and
    ///     config. Production code supplies a factory that builds a real
    ///     <see cref="LspClient"/>; tests supply a factory backed by in-memory streams.
    /// </param>
    public LspServerManager(
        IReadOnlyDictionary<string, LspServerConfig> configs,
        Func<string, LspServerConfig, LspServerInstance> instanceFactory)
    {
        this.configs = configs;
        this.instanceFactory = instanceFactory;
    }

    /// <summary>
    /// Builds the extension→server map, creates all server instances (does NOT start them),
    /// and registers the <c>workspace/configuration</c> request handler on each instance
    /// so servers that send it receive a valid response immediately.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct)
    {
        foreach (var (name, config) in this.configs)
        {
            var instance = this.instanceFactory(name, config);

            // Register workspace/configuration handler: respond with null for each item.
            instance.OnRequest("workspace/configuration", @params =>
            {
                if (@params is JsonObject paramsObj &&
                    paramsObj["items"] is JsonArray items)
                {
                    var result = new JsonArray();
                    foreach (var _ in items)
                    {
                        result.Add((JsonNode?)null);
                    }

                    return result;
                }

                return new JsonArray();
            });

            this.servers[name] = instance;

            foreach (var (ext, _) in config.ExtensionToLanguage)
            {
                var normalizedExt = ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
                if (!this.extensionMap.ContainsKey(normalizedExt))
                {
                    this.extensionMap[normalizedExt] = name;
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the first server configured to handle the given file's extension,
    /// or <see langword="null"/> if no server matches.
    /// </summary>
    public LspServerInstance? GetServerForFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (!this.extensionMap.TryGetValue(ext, out var serverName))
        {
            return null;
        }

        this.servers.TryGetValue(serverName, out var server);
        return server;
    }

    /// <summary>
    /// Returns the server for the given file, starting it if it is Stopped or in Error state.
    /// Returns <see langword="null"/> if no server handles the extension.
    /// </summary>
    public async Task<LspServerInstance?> EnsureServerStartedAsync(string filePath, CancellationToken ct)
    {
        var server = this.GetServerForFile(filePath);
        if (server is null)
        {
            return null;
        }

        if (server.State == LspServerState.Stopped || server.State == LspServerState.Error)
        {
            await server.StartAsync(ct).ConfigureAwait(false);
        }

        return server;
    }

    /// <summary>
    /// Routes a request to the server that handles the given file's extension.
    /// Returns <see langword="null"/> if no server handles the extension.
    /// </summary>
    public async Task<JsonNode?> SendRequestAsync(
        string filePath,
        string method,
        JsonNode? @params,
        CancellationToken ct)
    {
        var server = await this.EnsureServerStartedAsync(filePath, ct).ConfigureAwait(false);
        if (server is null)
        {
            return null;
        }

        return await server.SendRequestAsync(method, @params, ct).ConfigureAwait(false);
    }

    /// <summary>Returns a snapshot of all server instances keyed by name.</summary>
    public IReadOnlyDictionary<string, LspServerInstance> GetAllServers()
    {
        return this.servers;
    }

    /// <summary>
    /// A fresh, name-ordered, immutable snapshot of the configured servers for the UI status view:
    /// each server's name, current lifecycle state and the (sorted) file extensions it handles.
    /// Carries no <see cref="LspServerInstance"/> references, so the UI never touches engine state.
    /// </summary>
    public LspServerSnapshot[] GetSnapshot()
    {
        return this.servers
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new LspServerSnapshot(
                kv.Value.Name,
                kv.Value.State,
                this.extensionMap
                    .Where(e => string.Equals(e.Value, kv.Key, StringComparison.Ordinal))
                    .Select(e => e.Key)
                    .OrderBy(ext => ext, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Sends <c>textDocument/didOpen</c> for the file. Deduplicates: if the file is already
    /// open on the same server, the notification is not sent again.
    /// </summary>
    public async Task OpenFileAsync(string filePath, string content, CancellationToken ct)
    {
        var server = await this.EnsureServerStartedAsync(filePath, ct).ConfigureAwait(false);
        if (server is null)
        {
            return;
        }

        var uri = FileUri(filePath);

        if (this.openedFiles.TryGetValue(uri, out var openedOnServer) && openedOnServer == server.Name)
        {
            return;
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var languageId = server.Config.ExtensionToLanguage.TryGetValue(ext, out var lang)
            ? lang
            : "plaintext";

        await server.SendNotificationAsync(
            "textDocument/didOpen",
            new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = uri,
                    ["languageId"] = languageId,
                    ["version"] = 1,
                    ["text"] = content
                }
            },
            ct).ConfigureAwait(false);

        this.openedFiles[uri] = server.Name;
        this.fileVersions[uri] = 1;
    }

    /// <summary>
    /// Sends <c>textDocument/didChange</c>. If the file is not yet open, opens it first.
    /// </summary>
    public async Task ChangeFileAsync(string filePath, string content, CancellationToken ct)
    {
        var server = this.GetServerForFile(filePath);
        var uri = FileUri(filePath);

        if (server is null || server.State != LspServerState.Running ||
            !this.openedFiles.ContainsKey(uri))
        {
            await this.OpenFileAsync(filePath, content, ct).ConfigureAwait(false);
            return;
        }

        var version = this.fileVersions.TryGetValue(uri, out var v) ? v + 1 : 2;
        this.fileVersions[uri] = version;

        await server.SendNotificationAsync(
            "textDocument/didChange",
            new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = uri,
                    ["version"] = version
                },
                ["contentChanges"] = new JsonArray
                {
                    new JsonObject { ["text"] = content }
                }
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends <c>textDocument/didSave</c>. No-op if the server is not running.
    /// </summary>
    public async Task SaveFileAsync(string filePath, CancellationToken ct)
    {
        var server = this.GetServerForFile(filePath);
        if (server is null || server.State != LspServerState.Running)
        {
            return;
        }

        var uri = FileUri(filePath);

        await server.SendNotificationAsync(
            "textDocument/didSave",
            new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri }
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends <c>textDocument/didClose</c> and removes the file from the open-file set.
    /// No-op if the server is not running.
    /// </summary>
    public async Task CloseFileAsync(string filePath, CancellationToken ct)
    {
        var server = this.GetServerForFile(filePath);
        if (server is null || server.State != LspServerState.Running)
        {
            return;
        }

        var uri = FileUri(filePath);

        await server.SendNotificationAsync(
            "textDocument/didClose",
            new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri }
            },
            ct).ConfigureAwait(false);

        this.openedFiles.Remove(uri);
        this.fileVersions.Remove(uri);
    }

    /// <summary>Returns <see langword="true"/> if the file has been opened on any server.</summary>
    public bool IsFileOpen(string filePath)
    {
        var uri = FileUri(filePath);
        return this.openedFiles.ContainsKey(uri);
    }

    /// <summary>
    /// Stops all running/error servers, swallowing individual failures.
    /// Aggregates any failures into a single exception thrown at the end.
    /// Clears all internal state.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        var toStop = this.servers.Values
            .Where(s => s.State == LspServerState.Running || s.State == LspServerState.Error)
            .ToList();

        var results = await Task
            .WhenAll(toStop.Select(s => s.StopAsync(ct)))
            .ContinueWith(t => t, TaskScheduler.Default)
            .ConfigureAwait(false);

        this.servers.Clear();
        this.extensionMap.Clear();
        this.openedFiles.Clear();
        this.fileVersions.Clear();

        // Aggregate any failures.
        var exceptions = new List<Exception>();
        if (results.IsFaulted && results.Exception is not null)
        {
            exceptions.AddRange(results.Exception.InnerExceptions);
        }

        if (exceptions.Count > 0)
        {
            throw new AggregateException("One or more LSP servers failed to stop.", exceptions);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Swallow on dispose — already best-effort.
        }
    }

    private static string FileUri(string filePath)
    {
        return new Uri(Path.GetFullPath(filePath)).AbsoluteUri;
    }
}
