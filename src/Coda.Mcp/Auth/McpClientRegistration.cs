using System.Text.Json;

namespace Coda.Mcp.Auth;

/// <summary>
/// The credentials issued by an authorization server's Dynamic Client Registration
/// endpoint (RFC 7591), persisted per issuer so registration happens at most once.
/// </summary>
public sealed record McpClientRegistration(string ClientId, string? ClientSecret)
{
    public static McpClientRegistration? Parse(JsonElement root)
    {
        var clientId = root.TryGetProperty("client_id", out var id) ? id.GetString() : null;
        if (string.IsNullOrEmpty(clientId))
        {
            return null;
        }

        var secret = root.TryGetProperty("client_secret", out var s) ? s.GetString() : null;
        return new McpClientRegistration(clientId!, secret);
    }
}
