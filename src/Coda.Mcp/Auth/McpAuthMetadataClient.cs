using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coda.Mcp.Auth;

/// <summary>
/// Fetches the OAuth discovery documents and performs Dynamic Client Registration for the
/// MCP authorization flow: RFC 9728 protected-resource metadata, RFC 8414 / OpenID Connect
/// authorization-server metadata, and RFC 7591 client registration.
/// </summary>
public sealed class McpAuthMetadataClient
{
    private readonly HttpClient http;

    public McpAuthMetadataClient(HttpClient http)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>Fetch the RFC 9728 Protected Resource Metadata document.</summary>
    public async Task<ProtectedResourceMetadata?> GetProtectedResourceMetadataAsync(Uri metadataUrl, CancellationToken cancellationToken = default)
    {
        var doc = await this.GetJsonAsync(metadataUrl, cancellationToken).ConfigureAwait(false);
        return doc is null ? null : ProtectedResourceMetadata.Parse(doc.Value);
    }

    /// <summary>
    /// Fetch authorization-server metadata, trying RFC 8414 then OpenID Connect Discovery.
    /// The well-known path is appended to the issuer (after trimming any trailing slash).
    /// </summary>
    public async Task<AuthorizationServerMetadata?> GetAuthorizationServerMetadataAsync(string issuer, CancellationToken cancellationToken = default)
    {
        var baseUrl = issuer.TrimEnd('/');
        string[] candidates =
        [
            baseUrl + "/.well-known/oauth-authorization-server",
            baseUrl + "/.well-known/openid-configuration",
        ];

        foreach (var candidate in candidates)
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var doc = await this.GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
            var parsed = doc is null ? null : AuthorizationServerMetadata.Parse(doc.Value);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>Register a public native client via RFC 7591 Dynamic Client Registration.</summary>
    public async Task<McpClientRegistration?> RegisterClientAsync(
        string registrationEndpoint,
        string redirectUri,
        IReadOnlyList<string> grantTypes,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["client_name"] = "Coda CLI",
            ["redirect_uris"] = new JsonArray(redirectUri),
            ["grant_types"] = new JsonArray([.. grantTypes.Select(g => (JsonNode)g)]),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
            ["application_type"] = "native",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, registrationEndpoint)
        {
            Content = JsonContent.Create(body),
        };

        try
        {
            using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return McpClientRegistration.Parse(doc.RootElement.Clone());
        }
        catch (Exception ex) when (
            ex is HttpRequestException or JsonException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A transport failure, a non-caller timeout, a malformed 2xx body, or an invalid
            // client_id shape all mean "no usable client id" — surface null so the caller
            // reports the actionable, secret-free endpoint-specific failure. Genuine
            // caller-requested cancellation is excluded by the filter and propagates.
            return null;
        }
    }

    private async Task<JsonElement?> GetJsonAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await this.http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }
}
