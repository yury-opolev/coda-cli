using System.Text.Json;

namespace Coda.Mcp;

/// <summary>
/// Identity a server reports in its <c>initialize</c> result: <c>serverInfo.name</c> /
/// <c>version</c> and the optional human-facing <c>instructions</c> ("what this server does").
/// Any field the server omits is null.
/// </summary>
public sealed record McpServerInfo(string? Name, string? Version, string? Instructions)
{
    /// <summary>Parse the <c>initialize</c> result. A non-object or missing fields yield nulls.</summary>
    public static McpServerInfo Parse(JsonElement initializeResult)
    {
        if (initializeResult.ValueKind != JsonValueKind.Object)
        {
            return new McpServerInfo(null, null, null);
        }

        string? name = null;
        string? version = null;
        if (initializeResult.TryGetProperty("serverInfo", out var serverInfo) && serverInfo.ValueKind == JsonValueKind.Object)
        {
            name = GetString(serverInfo, "name");
            version = GetString(serverInfo, "version");
        }

        return new McpServerInfo(name, version, GetString(initializeResult, "instructions"));
    }

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
