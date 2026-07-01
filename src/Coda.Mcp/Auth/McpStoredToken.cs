namespace Coda.Mcp.Auth;

/// <summary>
/// A persisted MCP access token (and its refresh token), stored encrypted via the
/// <see cref="LlmAuth.ITokenStore"/> keyed by the canonical resource URI.
/// </summary>
public sealed record McpStoredToken(
    string AccessToken,
    string? RefreshToken,
    long ExpiresAtUnix,
    string Scope,
    string Issuer,
    string ClientId)
{
    /// <summary>True when the token is expired or within a 30s safety window of expiry.</summary>
    public bool IsExpired(DateTimeOffset now)
    {
        return this.ExpiresAtUnix > 0 && now.ToUnixTimeSeconds() >= this.ExpiresAtUnix - 30;
    }
}
