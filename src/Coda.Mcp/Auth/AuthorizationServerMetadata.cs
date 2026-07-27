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
    /// <summary>Parse the metadata document. Returns null when required endpoints are missing or have a disallowed scheme.</summary>
    public static AuthorizationServerMetadata? Parse(JsonElement root)
    {
        var issuer = root.TryGetProperty("issuer", out var i) ? i.GetString() : null;
        var authorize = root.TryGetProperty("authorization_endpoint", out var a) ? a.GetString() : null;
        var token = root.TryGetProperty("token_endpoint", out var t) ? t.GetString() : null;

        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(authorize) || string.IsNullOrEmpty(token))
        {
            return null;
        }

        // Validate endpoint schemes before constructing the record.  An attacker-controlled
        // .mcp.json can supply any URL as authorization_endpoint; ShellExecute on Windows
        // invokes ANY registered protocol handler (ms-msdt:, file://, search-ms:, …),
        // enabling Follina-class code execution with no confirmation prompt.
        if (!IsAllowedEndpointUri(authorize) || !IsAllowedEndpointUri(token))
        {
            return null;
        }

        var registration = root.TryGetProperty("registration_endpoint", out var reg) ? reg.GetString() : null;

        // registration_endpoint is optional; strip it when its scheme is hostile so that
        // a configured auth.clientId can still be used without dynamic client registration.
        if (registration is not null && !IsAllowedEndpointUri(registration))
        {
            registration = null;
        }

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

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is a non-empty absolute
    /// <c>https</c> URI, or an absolute <c>http</c> URI on a loopback host.
    /// Loopback <c>http</c> is accepted for local OAuth redirect/dev servers
    /// (<c>http://127.0.0.1</c>, <c>http://localhost</c>). Any other scheme, including
    /// <c>ms-msdt:</c>, <c>file://</c>, and non-loopback <c>http</c>, is rejected to
    /// prevent ShellExecute-based protocol-handler invocation.
    /// </summary>
    private static bool IsAllowedEndpointUri(string? value)
    {
        if (string.IsNullOrEmpty(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) && uri.IsLoopback;
    }
}
