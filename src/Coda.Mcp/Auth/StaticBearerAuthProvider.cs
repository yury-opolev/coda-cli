namespace Coda.Mcp.Auth;

/// <summary>
/// An <see cref="IMcpAuthProvider"/> that attaches a fixed bearer token (the
/// <c>auth.mode = "bearer"</c> case). It cannot recover from a 401.
/// </summary>
public sealed class StaticBearerAuthProvider : IMcpAuthProvider
{
    private readonly string token;

    public StaticBearerAuthProvider(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        this.token = token;
    }

    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(this.token);
    }

    public Task<bool> HandleUnauthorizedAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}
