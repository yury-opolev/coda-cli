namespace Coda.Mcp.Auth;

/// <summary>
/// Supplies and refreshes the bearer token an <see cref="McpHttpClient"/> attaches to its
/// requests. <see cref="McpHttpClient"/> calls <see cref="GetAccessTokenAsync"/> before each
/// request and, on an HTTP 401, calls <see cref="HandleUnauthorizedAsync"/> to (re)authorize
/// and then retries once.
/// </summary>
public interface IMcpAuthProvider
{
    /// <summary>Return a currently valid access token, refreshing if needed, or null if none.</summary>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// React to a 401 from the server (parse the challenge, run the auth flow, persist the
    /// token). Returns true when a token is now available and the request should be retried.
    /// </summary>
    Task<bool> HandleUnauthorizedAsync(HttpResponseMessage response, CancellationToken cancellationToken = default);
}
