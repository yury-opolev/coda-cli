using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coda.Mcp;

/// <summary>
/// Writes MCP server entries to a scope's <c>.mcp.json</c>, preserving all other content
/// (other servers and any unrelated top-level keys). The single mutation point behind
/// <c>/mcp add | edit | remove | enable | disable</c>. Serialization mirrors the shapes
/// <see cref="McpConfig.Parse"/> accepts, so writes round-trip loss-free.
/// </summary>
public static class McpConfigWriter
{
    private static readonly JsonSerializerOptions writeOptions = new() { WriteIndented = true };
    private static readonly string[] knownEntryProperties =
        ["type", "command", "args", "env", "url", "headers", "auth", "disabled"];

    /// <summary>Add or replace <paramref name="name"/> in the scope's file (creating the file if needed).</summary>
    public static void Upsert(
        McpConfigScope scope, string name, McpServerConfig config, bool disabled,
        string workingDirectory, string? userMcpDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);

        var path = McpConfig.FilePath(scope, workingDirectory, userMcpDir);
        var root = ReadRoot(path);
        GetOrCreateServers(root)[name] = ToJson(config, disabled);
        Write(path, root);
    }

    /// <summary>Remove <paramref name="name"/> from the scope's file. Returns false when absent.</summary>
    public static bool Remove(McpConfigScope scope, string name, string workingDirectory, string? userMcpDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var path = McpConfig.FilePath(scope, workingDirectory, userMcpDir);
        if (!File.Exists(path))
        {
            return false;
        }

        var root = ReadRoot(path);
        if (root["mcpServers"] is not JsonObject servers || !servers.ContainsKey(name))
        {
            return false;
        }

        servers.Remove(name);
        Write(path, root);
        return true;
    }

    /// <summary>Set or clear the persisted <c>disabled</c> flag on an existing entry. Returns false when absent.</summary>
    public static bool SetDisabled(
        McpConfigScope scope, string name, bool disabled, string workingDirectory, string? userMcpDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var path = McpConfig.FilePath(scope, workingDirectory, userMcpDir);
        if (!File.Exists(path))
        {
            return false;
        }

        var root = ReadRoot(path);
        if (root["mcpServers"] is not JsonObject servers || servers[name] is not JsonObject entry)
        {
            return false;
        }

        if (disabled)
        {
            entry["disabled"] = true;
        }
        else
        {
            entry.Remove("disabled");
        }

        Write(path, root);
        return true;
    }

    /// <summary>
    /// Replace an existing server entry, optionally renaming it, while preserving unknown entry
    /// properties and all unrelated configuration.
    /// </summary>
    public static void ReplaceEntry(
        McpConfigScope scope, string currentName, string newName,
        McpServerConfig config, bool disabled, string workingDirectory,
        string? userMcpDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ArgumentNullException.ThrowIfNull(config);

        var path = McpConfig.FilePath(scope, workingDirectory, userMcpDir);
        var root = ReadExistingRoot(path);
        var servers = GetStrictServers(root, path);
        if (servers[currentName] is not JsonObject existing)
        {
            throw new McpException($"MCP server '{currentName}' does not exist in config '{path}'.");
        }

        if (!string.Equals(currentName, newName, StringComparison.Ordinal) && servers.ContainsKey(newName))
        {
            throw new McpException($"MCP server '{newName}' already exists in config '{path}'.");
        }

        var replacement = existing.DeepClone().AsObject();
        var serialized = ToJson(config, disabled);
        foreach (var property in knownEntryProperties)
        {
            replacement.Remove(property);
        }

        foreach (var property in serialized)
        {
            replacement[property.Key] = property.Value?.DeepClone();
        }

        if (string.Equals(currentName, newName, StringComparison.Ordinal))
        {
            servers[currentName] = replacement;
        }
        else
        {
            servers.Remove(currentName);
            servers[newName] = replacement;
        }

        Write(path, root);
    }

    private static JsonObject ReadRoot(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject(); // brand-new config file
        }

        // NEVER start fresh on an existing-but-unparseable file: a mutating write would then discard
        // every other server and unrelated key. Refuse instead, so the file is preserved.
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new McpException($"'{path}' is not valid JSON ({ex.Message}); fix or remove it before editing MCP servers.");
        }

        return root as JsonObject
            ?? throw new McpException($"'{path}' does not contain a JSON object; fix or remove it before editing MCP servers.");
    }

    private static JsonObject ReadExistingRoot(string path)
    {
        if (!File.Exists(path))
        {
            throw new McpException($"MCP config '{path}' does not exist.");
        }

        return ReadRoot(path);
    }

    private static JsonObject GetStrictServers(JsonObject root, string path)
    {
        if (!root.TryGetPropertyValue("mcpServers", out var serversNode) || serversNode is not JsonObject servers)
        {
            throw new McpException($"MCP config '{path}' must contain an mcpServers object.");
        }

        foreach (var server in servers)
        {
            if (server.Value is not JsonObject entry)
            {
                throw new McpException(
                    $"MCP server '{server.Key}' in config '{path}' has an invalid definition: server values must be JSON objects.");
            }

            ValidateServer(entry, server.Key, path);
        }

        return servers;
    }

    private static void ValidateServer(JsonObject entry, string name, string path)
    {
        var type = entry["type"]?.GetValue<string>();
        var valid = type switch
        {
            "http" or "streamable-http" => entry["url"]?.GetValue<string>() is { Length: > 0 } url
                && Uri.TryCreate(url, UriKind.Absolute, out _),
            null or "stdio" => entry["command"]?.GetValue<string>() is { Length: > 0 },
            _ => false,
        };

        if (!valid)
        {
            throw new McpException($"MCP server '{name}' in config '{path}' has an invalid definition.");
        }
    }

    private static JsonObject GetOrCreateServers(JsonObject root)
    {
        if (root["mcpServers"] is JsonObject servers)
        {
            return servers;
        }

        var created = new JsonObject();
        root["mcpServers"] = created;
        return created;
    }

    private static void Write(string path, JsonObject root)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a unique same-directory temp file then move over the target, so concurrent
        // writers do not collide and a crash mid-write cannot truncate the config.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, root.ToJsonString(writeOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static JsonObject ToJson(McpServerConfig config, bool disabled)
    {
        var obj = config switch
        {
            McpStdioServerConfig stdio => StdioJson(stdio),
            McpHttpServerConfig http => HttpJson(http),
            _ => new JsonObject(),
        };

        if (disabled)
        {
            obj["disabled"] = true;
        }

        return obj;
    }

    private static JsonObject StdioJson(McpStdioServerConfig config)
    {
        var obj = new JsonObject { ["command"] = config.Command };
        if (config.Args.Count > 0)
        {
            obj["args"] = ToJsonArray(config.Args);
        }

        if (config.Env.Count > 0)
        {
            obj["env"] = ToJsonObject(config.Env);
        }

        return obj;
    }

    private static JsonObject HttpJson(McpHttpServerConfig config)
    {
        var obj = new JsonObject
        {
            ["type"] = "http",
            ["url"] = config.Url.ToString(),
        };

        if (config.Headers.Count > 0)
        {
            obj["headers"] = ToJsonObject(config.Headers);
        }

        var auth = AuthJson(config.Auth);
        if (auth is not null)
        {
            obj["auth"] = auth;
        }

        return obj;
    }

    private static JsonObject? AuthJson(McpAuthConfig auth)
    {
        // Omit the block for the default (plain OAuth, no client id / scopes / token) — it round-trips to Default.
        var isDefault = auth.Mode == McpAuthMode.OAuth
            && auth.ClientId is null
            && (auth.Scopes is null || auth.Scopes.Count == 0)
            && auth.BearerToken is null;
        if (isDefault)
        {
            return null;
        }

        var obj = new JsonObject { ["mode"] = auth.Mode.ToString().ToLowerInvariant() };
        if (auth.ClientId is not null)
        {
            obj["clientId"] = auth.ClientId;
        }

        if (auth.Scopes is { Count: > 0 })
        {
            obj["scopes"] = ToJsonArray(auth.Scopes);
        }

        if (auth.BearerToken is not null)
        {
            obj["token"] = auth.BearerToken;
        }

        return obj;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(JsonValue.Create(value));
        }

        return array;
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map)
        {
            obj[key] = value;
        }

        return obj;
    }
}
