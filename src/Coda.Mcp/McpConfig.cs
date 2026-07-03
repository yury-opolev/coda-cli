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
    public static IReadOnlyDictionary<string, McpServerConfig> Load(string workingDirectory, string? userMcpDir = null)
    {
        var (userServers, projectServers) = LoadLayers(workingDirectory, userMcpDir);

        if (userServers.Count == 0)
        {
            return projectServers;
        }

        // Merge: user first, then project overlays by name (project wins).
        var merged = new Dictionary<string, McpServerConfig>(userServers, StringComparer.Ordinal);
        foreach (var (name, config) in projectServers)
        {
            merged[name] = config;
        }

        return merged;
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

    /// <summary>Resolve and parse the user and project layers separately (shared by Load/LoadEntries).</summary>
    private static (IReadOnlyDictionary<string, McpServerConfig> User, IReadOnlyDictionary<string, McpServerConfig> Project) LoadLayers(
        string workingDirectory, string? userMcpDir)
    {
        var userBase = userMcpDir
            ?? Environment.GetEnvironmentVariable("CODA_USER_MCP_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".coda");

        return (
            LoadFile(Path.Combine(userBase, FileName)),
            LoadFile(Path.Combine(workingDirectory, FileName)));
    }

    private static IReadOnlyDictionary<string, McpServerConfig> LoadFile(string path)
    {
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : new Dictionary<string, McpServerConfig>();
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
        return type switch
        {
            "http" or "streamable-http" => ParseHttp(config),
            null or "stdio" => ParseStdio(config),
            _ => null, // unknown transport (e.g. legacy "sse") is skipped
        };
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
