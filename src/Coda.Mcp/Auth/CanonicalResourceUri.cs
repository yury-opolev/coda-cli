namespace Coda.Mcp.Auth;

/// <summary>
/// Computes the RFC 8707 / RFC 9728 canonical resource identifier for an MCP server URL:
/// lowercase scheme and host, no fragment, default ports elided, and no trailing slash
/// (unless the path is exactly "/"). Used as the <c>resource</c> parameter and as the
/// token-store key so the same server shares one token across configs.
/// </summary>
public static class CanonicalResourceUri
{
    public static string From(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var builder = new UriBuilder(url)
        {
            Scheme = url.Scheme.ToLowerInvariant(),
            Host = url.Host.ToLowerInvariant(),
            Fragment = string.Empty,
        };

        // Elide the default port so https://h:443 and https://h are the same resource.
        if (url.IsDefaultPort)
        {
            builder.Port = -1;
        }

        var result = builder.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path | UriComponents.Query,
            UriFormat.UriEscaped);

        // Drop a single trailing slash, but keep the bare-origin "/" meaningful-free form.
        if (result.EndsWith('/'))
        {
            result = result[..^1];
        }

        return result;
    }
}
