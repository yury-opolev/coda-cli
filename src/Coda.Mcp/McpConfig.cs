using System.Text.Json;

namespace Coda.Mcp;

/// <summary>
/// Loads stdio MCP server definitions from <c>.mcp.json</c> in the working
/// directory: <c>{ "mcpServers": { name: { command, args[], env{}, type? } } }</c>.
/// Only stdio servers (no <c>type</c>, or <c>type:"stdio"</c>) are returned.
/// </summary>
public static class McpConfig
{
    public static IReadOnlyDictionary<string, McpServerConfig> Load(string workingDirectory)
    {
        var path = Path.Combine(workingDirectory, ".mcp.json");
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
                var config = server.Value;
                var type = config.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type is not null and not "stdio")
                {
                    continue; // only stdio servers are supported here
                }

                var command = config.TryGetProperty("command", out var c) ? c.GetString() : null;
                if (string.IsNullOrEmpty(command))
                {
                    continue;
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

                var env = new Dictionary<string, string>(StringComparer.Ordinal);
                if (config.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object)
                {
                    foreach (var pair in e.EnumerateObject())
                    {
                        if (pair.Value.ValueKind == JsonValueKind.String)
                        {
                            env[pair.Name] = pair.Value.GetString()!;
                        }
                    }
                }

                result[server.Name] = new McpServerConfig(command, args, env);
            }
        }

        return result;
    }
}
