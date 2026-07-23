using System.Text.Json;

namespace Coda.Mcp;

/// <summary>
/// Loads MCP server definitions from <c>.mcp.json</c> files:
/// <c>{ "mcpServers": { name: { command, args[], env{}, type? } | { type:"http", url, headers{}, auth{} } } }</c>.
/// <para>
/// Two layers are merged, mirroring how skills and <c>settings.json</c> resolve:
/// a user file at <c>~/.coda/.mcp.json</c> (lowest precedence) and a project file at
/// <c>&lt;workingDirectory&gt;/.mcp.json</c> (highest). Project entries override user
/// entries by name.
/// </para>
/// </summary>
public static class McpConfig
{
    private const string FileName = ".mcp.json";

    /// <summary>
    /// Load and merge the user (<c>~/.coda/.mcp.json</c>) and project
    /// (<c>&lt;workingDirectory&gt;/.mcp.json</c>) server definitions. Project entries
    /// override user entries by name.
    /// </summary>
    /// <param name="workingDirectory">The project directory holding <c>.mcp.json</c>.</param>
    /// <param name="userMcpDir">
    /// The directory holding the user-level <c>.mcp.json</c>. Defaults to
    /// <c>CODA_USER_MCP_DIR</c> or <c>~/.coda</c> when null.
    /// </param>
    /// <param name="includeProject">When false, the project layer (<c>&lt;cwd&gt;/.mcp.json</c>) is
    /// ignored entirely — used by <c>coda serve --no-project-mcp</c> so an orchestrator-curated user
    /// set can't be overridden by a repo-local file.</param>
    public static IReadOnlyDictionary<string, McpServerConfig> Load(string workingDirectory, string? userMcpDir = null, bool includeProject = true)
    {
        var (userServers, projectServers) = LoadLayers(workingDirectory, userMcpDir);

        // Merge: user first, then project overlays by name (project wins) unless suppressed.
        var merged = new Dictionary<string, McpServerConfig>(userServers, StringComparer.Ordinal);
        if (includeProject)
        {
            foreach (var (name, config) in projectServers)
            {
                merged[name] = config;
            }
        }

        // Disabled servers are excluded so they never auto-connect; they remain visible via
        // LoadEntries so /mcp can list and re-enable them.
        var connectable = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var (name, config) in merged)
        {
            if (!config.Disabled)
            {
                connectable[name] = config;
            }
        }

        return connectable;
    }

    /// <summary>
    /// Like <see cref="Load"/> but tags each server with the <see cref="McpConfigScope"/> it was
    /// resolved from (project overrides user). For display and scope-aware editing.
    /// </summary>
    public static IReadOnlyList<McpServerEntry> LoadEntries(string workingDirectory, string? userMcpDir = null)
    {
        var (userServers, projectServers) = LoadLayers(workingDirectory, userMcpDir);
        var entries = new List<McpServerEntry>();

        foreach (var (name, config) in userServers)
        {
            if (!projectServers.ContainsKey(name))
            {
                entries.Add(new McpServerEntry(name, config, McpConfigScope.User));
            }
        }

        foreach (var (name, config) in projectServers)
        {
            entries.Add(new McpServerEntry(name, config, McpConfigScope.Project));
        }

        return entries;
    }

    /// <summary>
    /// Load every physical MCP server definition from the user and optionally project scopes.
    /// User entries are returned first, followed by project entries, preserving each file's JSON
    /// property order. A project definition is effective whenever it exists, including when disabled.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Load"/> and <see cref="LoadEntries"/>, this read model reports malformed
    /// existing configuration files rather than treating them as empty.
    /// </remarks>
    public static IReadOnlyList<McpPhysicalServerEntry> LoadPhysicalEntries(
        string workingDirectory,
        string? userMcpDir = null,
        bool includeProject = true)
    {
        var userPath = FilePath(McpConfigScope.User, workingDirectory, userMcpDir);
        var userServers = LoadPhysicalFile(userPath);
        var projectPath = FilePath(McpConfigScope.Project, workingDirectory, userMcpDir);
        var projectServers = includeProject
            ? LoadPhysicalFile(projectPath)
            : new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);

        var entries = new List<McpPhysicalServerEntry>(userServers.Count + projectServers.Count);
        foreach (var (name, config) in userServers)
        {
            entries.Add(new McpPhysicalServerEntry(
                new McpServerKey(McpConfigScope.User, name),
                config,
                userPath,
                !projectServers.ContainsKey(name)));
        }

        foreach (var (name, config) in projectServers)
        {
            entries.Add(new McpPhysicalServerEntry(
                new McpServerKey(McpConfigScope.Project, name),
                config,
                projectPath,
                true));
        }

        return entries;
    }

    /// <summary>
    /// The <c>.mcp.json</c> path for a given scope: the working directory for
    /// <see cref="McpConfigScope.Project"/>, or the user base
    /// (<paramref name="userMcpDir"/> ?? <c>CODA_USER_MCP_DIR</c> ?? <c>~/.coda</c>) for
    /// <see cref="McpConfigScope.User"/>. Shared by the loader and the writer.
    /// </summary>
    public static string FilePath(McpConfigScope scope, string workingDirectory, string? userMcpDir = null)
    {
        var directory = scope == McpConfigScope.User
            ? userMcpDir
                ?? Environment.GetEnvironmentVariable("CODA_USER_MCP_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".coda")
            : workingDirectory;

        return Path.Combine(directory, FileName);
    }

    /// <summary>Resolve and parse the user and project layers separately (shared by Load/LoadEntries).</summary>
    private static (IReadOnlyDictionary<string, McpServerConfig> User, IReadOnlyDictionary<string, McpServerConfig> Project) LoadLayers(
        string workingDirectory, string? userMcpDir)
    {
        return (
            LoadFile(FilePath(McpConfigScope.User, workingDirectory, userMcpDir)),
            LoadFile(FilePath(McpConfigScope.Project, workingDirectory, userMcpDir)));
    }

    private static IReadOnlyDictionary<string, McpServerConfig> LoadFile(string path)
    {
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : new Dictionary<string, McpServerConfig>();
    }

    private static IReadOnlyDictionary<string, McpServerConfig> LoadPhysicalFile(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new McpException($"MCP config '{path}' must contain valid JSON.", exception);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new McpException($"MCP config '{path}' must be a JSON object.");
            }

            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) ||
                servers.ValueKind != JsonValueKind.Object)
            {
                throw new McpException($"MCP config '{path}' must contain an mcpServers object.");
            }

            var result = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
            foreach (var server in servers.EnumerateObject())
            {
                var config = ParseServer(server.Value);
                if (config is not null)
                {
                    result[server.Name] = config;
                }
            }

            return result;
        }
    }

    public static IReadOnlyDictionary<string, McpServerConfig> Parse(string json)
    {
        var result = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var server in servers.EnumerateObject())
            {
                var config = ParseServer(server.Value);
                if (config is not null)
                {
                    result[server.Name] = config;
                }
            }
        }

        return result;
    }

    private static McpServerConfig? ParseServer(JsonElement config)
    {
        var type = config.TryGetProperty("type", out var t) ? t.GetString() : null;
        var parsed = type switch
        {
            "http" or "streamable-http" => ParseHttp(config),
            null or "stdio" => ParseStdio(config),
            _ => (McpServerConfig?)null, // unknown transport (e.g. legacy "sse") is skipped
        };

        if (parsed is null)
        {
            return null;
        }

        var disabled = config.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True;
        return disabled ? parsed with { Disabled = true } : parsed;
    }

    private static McpStdioServerConfig? ParseStdio(JsonElement config)
    {
        var command = config.TryGetProperty("command", out var c) ? c.GetString() : null;
        if (string.IsNullOrEmpty(command))
        {
            return null;
        }

        var args = new List<string>();
        if (config.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
        {
            foreach (var arg in a.EnumerateArray())
            {
                if (arg.ValueKind == JsonValueKind.String)
                {
                    args.Add(arg.GetString()!);
                }
            }
        }

        return new McpStdioServerConfig(command, args, ParseStringMap(config, "env"));
    }

    private static McpHttpServerConfig? ParseHttp(JsonElement config)
    {
        var url = config.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return new McpHttpServerConfig(uri, ParseStringMap(config, "headers"), ParseAuth(config));
    }

    private static McpAuthConfig ParseAuth(JsonElement config)
    {
        if (!config.TryGetProperty("auth", out var auth) || auth.ValueKind != JsonValueKind.Object)
        {
            return McpAuthConfig.Default;
        }

        var mode = auth.TryGetProperty("mode", out var m) ? m.GetString() : null;
        var parsedMode = mode?.ToLowerInvariant() switch
        {
            "none" => McpAuthMode.None,
            "bearer" => McpAuthMode.Bearer,
            _ => McpAuthMode.OAuth,
        };

        var clientId = auth.TryGetProperty("clientId", out var ci) ? ci.GetString() : null;
        var token = auth.TryGetProperty("token", out var tok) ? tok.GetString() : null;

        List<string>? scopes = null;
        if (auth.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array)
        {
            scopes = [];
            foreach (var scope in sc.EnumerateArray())
            {
                if (scope.ValueKind == JsonValueKind.String)
                {
                    scopes.Add(scope.GetString()!);
                }
            }
        }

        return new McpAuthConfig(parsedMode, clientId, scopes, token);
    }

    private static IReadOnlyDictionary<string, string> ParseStringMap(JsonElement config, string property)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config.TryGetProperty(property, out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var pair in obj.EnumerateObject())
            {
                if (pair.Value.ValueKind == JsonValueKind.String)
                {
                    map[pair.Name] = pair.Value.GetString()!;
                }
            }
        }

        return map;
    }
}
