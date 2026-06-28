using System.Text.Json;

namespace Coda.Mcp.Auth;

/// <summary>
/// OAuth 2.0 Authorization Server Metadata (RFC 8414) or OpenID Connect Discovery 1.0
/// document. Both share the endpoint field names this record reads.
/// </summary>
public sealed record AuthorizationServerMetadata(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RegistrationEndpoint,
    IReadOnlyList<string> ScopesSupported,
    bool IssuerParameterSupported)
{
    /// <summary>Parse the metadata document. Returns null when required endpoints are missing.</summary>
    public static AuthorizationServerMetadata? Parse(JsonElement root)
    {
        var issuer = root.TryGetProperty("issuer", out var i) ? i.GetString() : null;
        var authorize = root.TryGetProperty("authorization_endpoint", out var a) ? a.GetString() : null;
        var token = root.TryGetProperty("token_endpoint", out var t) ? t.GetString() : null;

        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(authorize) || string.IsNullOrEmpty(token))
        {
            return null;
        }

        var registration = root.TryGetProperty("registration_endpoint", out var reg) ? reg.GetString() : null;
        var issParam = root.TryGetProperty("authorization_response_iss_parameter_supported", out var iss)
            && iss.ValueKind == JsonValueKind.True;

        return new AuthorizationServerMetadata(
            issuer!,
            authorize!,
            token!,
            registration,
            ProtectedResourceMetadata.ReadStringArray(root, "scopes_supported"),
            issParam);
    }
}
