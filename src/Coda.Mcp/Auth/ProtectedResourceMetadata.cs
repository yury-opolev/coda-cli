using System.Text.Json;

namespace Coda.Mcp.Auth;

/// <summary>
/// OAuth 2.0 Protected Resource Metadata (RFC 9728), served by an MCP server at
/// <c>/.well-known/oauth-protected-resource</c>. Tells the client which authorization
/// server(s) issue tokens for the resource, and which scopes it supports.
/// </summary>
public sealed record ProtectedResourceMetadata(
    string? Resource,
    IReadOnlyList<string> AuthorizationServers,
    IReadOnlyList<string> ScopesSupported)
{
    public static ProtectedResourceMetadata Parse(JsonElement root)
    {
        var resource = root.TryGetProperty("resource", out var r) ? r.GetString() : null;
        return new ProtectedResourceMetadata(
            resource,
            ReadStringArray(root, "authorization_servers"),
            ReadStringArray(root, "scopes_supported"));
    }

    internal static IReadOnlyList<string> ReadStringArray(JsonElement root, string property)
    {
        var list = new List<string>();
        if (root.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    list.Add(item.GetString()!);
                }
            }
        }

        return list;
    }
}
