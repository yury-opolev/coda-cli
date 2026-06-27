using System.Net.Http.Json;
using System.Text.Json;

namespace LlmAuth;

/// <summary>
/// The shared OAuth2 + PKCE engine reused by every OAuth provider: builds the
/// authorize URL and posts JSON to the token endpoint. Provider-specific
/// constants/bodies are supplied by the caller, so Claude.ai, and later Copilot
/// / OpenAI, reuse this without inheriting Anthropic specifics.
/// </summary>
public sealed class OAuth2PkceClient
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;

    public OAuth2PkceClient(HttpClient http)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Build an authorize URL by appending the given ordered query params to the
    /// base URL. Null-valued params are skipped. Values are URL-encoded.
    /// </summary>
    public static Uri BuildAuthorizeUrl(string authorizeBaseUrl, IEnumerable<KeyValuePair<string, string?>> query)
    {
        ArgumentException.ThrowIfNullOrEmpty(authorizeBaseUrl);
        var builder = new UriBuilder(authorizeBaseUrl);
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(builder.Query))
        {
            parts.Add(builder.Query.TrimStart('?'));
        }

        foreach (var (key, value) in query)
        {
            if (value is null)
            {
                continue;
            }

            parts.Add($"{FormUrlEncode(key)}={FormUrlEncode(value)}");
        }

        builder.Query = string.Join("&", parts);
        return builder.Uri;
    }

    /// <summary>
    /// Encode a query value the way the browser/URLSearchParams does
    /// (application/x-www-form-urlencoded): RFC 3986 percent-encoding, but spaces
    /// become <c>+</c>. The real client builds the authorize URL with
    /// <c>URLSearchParams</c>, so the scope list's separators must be <c>+</c>,
    /// not <c>%20</c>, for a byte-identical request.
    /// </summary>
    private static string FormUrlEncode(string value)
    {
        return Uri.EscapeDataString(value).Replace("%20", "+");
    }

    /// <summary>
    /// POST a JSON body to the token endpoint and deserialize the response.
    /// Matches the source: <c>Content-Type: application/json</c>; non-2xx throws
    /// <see cref="OAuthExchangeException"/> carrying the body for diagnostics.
    /// </summary>
    public async Task<OAuthTokenResponse> PostTokenAsync(
        string tokenUrl,
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenUrl);

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = JsonContent.Create(body, options: jsonOptions),
        };

        using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthExchangeException((int)response.StatusCode, text);
        }

        var parsed = JsonSerializer.Deserialize<OAuthTokenResponse>(text, jsonOptions);
        return parsed ?? throw new LlmAuthException("Token endpoint returned an empty/invalid body.");
    }
}
