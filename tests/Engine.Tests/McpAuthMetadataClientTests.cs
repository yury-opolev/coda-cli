using System.Net;
using System.Text;
using System.Text.Json;
using Coda.Mcp.Auth;

namespace Engine.Tests;

/// <summary>
/// Covers <see cref="McpAuthMetadataClient"/> — the OAuth discovery + Dynamic Client
/// Registration HTTP client (RFC 9728 / RFC 8414 / OpenID Discovery / RFC 7591).
/// </summary>
public sealed class McpAuthMetadataClientTests
{
    [Fact]
    public void Constructor_rejects_null_http_client()
    {
        Assert.Throws<ArgumentNullException>(() => new McpAuthMetadataClient(null!));
    }

    [Fact]
    public async Task GetProtectedResourceMetadata_parses_servers_and_scopes()
    {
        var handler = new RouteHandler();
        handler.Get("https://mcp.example.com/.well-known/oauth-protected-resource", """
            {"resource":"https://mcp.example.com","authorization_servers":["https://auth.example.com"],
             "scopes_supported":["files:read"]}
            """);

        using var http = new HttpClient(handler);
        var client = new McpAuthMetadataClient(http);

        var prm = await client.GetProtectedResourceMetadataAsync(
            new Uri("https://mcp.example.com/.well-known/oauth-protected-resource"));

        Assert.NotNull(prm);
        Assert.Equal("https://auth.example.com", Assert.Single(prm!.AuthorizationServers));
        Assert.Equal("files:read", Assert.Single(prm.ScopesSupported));
    }

    [Fact]
    public async Task GetProtectedResourceMetadata_returns_null_on_http_error()
    {
        var handler = new RouteHandler(); // nothing registered -> 404 by default
        using var http = new HttpClient(handler);
        var client = new McpAuthMetadataClient(http);

        Assert.Null(await client.GetProtectedResourceMetadataAsync(
            new Uri("https://mcp.example.com/.well-known/oauth-protected-resource")));
    }

    [Fact]
    public async Task GetProtectedResourceMetadata_swallows_transport_failures()
    {
        var handler = new RouteHandler { ThrowOnEveryRequest = true };
        using var http = new HttpClient(handler);
        var client = new McpAuthMetadataClient(http);

        // A thrown HttpRequestException must be caught and surfaced as null, not propagated.
        Assert.Null(await client.GetProtectedResourceMetadataAsync(new Uri("https://mcp.example.com/x")));
    }

    [Fact]
    public async Task GetProtectedResourceMetadata_returns_null_on_malformed_json()
    {
        var handler = new RouteHandler();
        handler.Get("https://mcp.example.com/x", "{ this is not json");

        using var http = new HttpClient(handler);
        var client = new McpAuthMetadataClient(http);

        // A 200 with an unparseable body hits the JsonException catch arm -> null, not a throw.
        Assert.Null(await client.GetProtectedResourceMetadataAsync(new Uri("https://mcp.example.com/x")));
    }

    [Fact]
    public async Task GetProtectedResourceMetadata_returns_null_when_cancelled()
    {
        var handler = new RouteHandler();
        handler.Get("https://mcp.example.com/x", """{"resource":"https://mcp.example.com"}""");

        using var http = new HttpClient(handler);
        var client = new McpAuthMetadataClient(http);
        var cancelled = new CancellationToken(canceled: true);

        // The TaskCanceledException catch arm swallows cancellation and yields null.
        Assert.Null(await client.GetProtectedResourceMetadataAsync(new Uri("https://mcp.example.com/x"), cancelled));
    }

    [Fact]
    public async Task GetAuthorizationServerMetadata_returns_null_for_non_absolute_issuer_without_calling_http()
    {
        var handler = new RouteHandler();
        using var http = new HttpClient(handler);
        var client = new McpAuthMetadataClient(http);

        // A non-absolute issuer makes both candidate URLs fail Uri.TryCreate(Absolute) -> skipped.
        Assert.Null(await client.GetAuthorizationServerMetadataAsync("not-a-valid-issuer"));
        Assert.Empty(handler.SeenUrls);
    }

    [Fact]
    public async Task GetAuthorizationServerMetadata_tries_rfc8414_first()
    {
        var handler = new RouteHandler();
        handler.Get("https://auth.example.com/.well-known/oauth-authorization-server", AuthDoc());

        using var http = new HttpClient(handler);
        var client = new McpAuthMetadataClient(http);

        var meta = await client.GetAuthorizationServerMetadataAsync("https://auth.example.com/");

        Assert.NotNull(meta);
        Assert.Equal("https://auth.example.com/token", meta!.TokenEndpoint);
        // The trailing slash on the issuer must be trimmed before appending the well-known path.
        Assert.Contains("https://auth.example.com/.well-known/oauth-authorization-server", handler.SeenUrls);
    }

    [Fact]
    public async Task GetAuthorizationServerMetadata_falls_back_to_openid_when_rfc8414_unusable()
    {
        var handler = new RouteHandler();
        // First candidate returns 200 but is missing endpoints -> Parse yields null -> must continue.
        handler.Get("https://auth.example.com/.well-known/oauth-authorization-server", """{"issuer":"https://auth.example.com"}""");
        handler.Get("https://auth.example.com/.well-known/openid-configuration", AuthDoc());

        using var http = new HttpClient(handler);
        var client = new McpAuthMetadataClient(http);

        var meta = await client.GetAuthorizationServerMetadataAsync("https://auth.example.com");

        Assert.NotNull(meta);
        Assert.Equal("https://auth.example.com/authorize", meta!.AuthorizationEndpoint);
        // Both candidates were attempted, in order.
        Assert.Equal(
            ["https://auth.example.com/.well-known/oauth-authorization-server",
             "https://auth.example.com/.well-known/openid-configuration"],
            handler.SeenUrls);
    }

    [Fact]
    public async Task GetAuthorizationServerMetadata_returns_null_when_both_candidates_fail()
    {
        var handler = new RouteHandler(); // both 404
        using var http = new HttpClient(handler);
        var client = new McpAuthMetadataClient(http);

        Assert.Null(await client.GetAuthorizationServerMetadataAsync("https://auth.example.com"));
        Assert.Equal(2, handler.SeenUrls.Count);
    }

    [Fact]
    public async Task RegisterClient_posts_native_public_client_body_and_parses_credentials()
    {
        var handler = new RouteHandler();
        handler.Post("https://auth.example.com/register", """{"client_id":"cid-123","client_secret":"shh"}""");

        using var http = new HttpClient(handler);
        var client = new McpAuthMetadataClient(http);

        var registration = await client.RegisterClientAsync(
            "https://auth.example.com/register",
            "http://127.0.0.1:5000/callback",
            ["authorization_code", "refresh_token"]);

        Assert.NotNull(registration);
        Assert.Equal("cid-123", registration!.ClientId);
        Assert.Equal("shh", registration.ClientSecret);

        // Verify the registration request body matches the RFC 7591 public-native-client shape.
        var body = JsonDocument.Parse(handler.LastPostBody!).RootElement;
        Assert.Equal("Coda CLI", body.GetProperty("client_name").GetString());
        Assert.Equal("none", body.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal("native", body.GetProperty("application_type").GetString());
        Assert.Equal("http://127.0.0.1:5000/callback", body.GetProperty("redirect_uris")[0].GetString());
        Assert.Equal(
            ["authorization_code", "refresh_token"],
            body.GetProperty("grant_types").EnumerateArray().Select(g => g.GetString()!).ToArray());
        Assert.Equal("code", body.GetProperty("response_types")[0].GetString());
    }

    [Fact]
    public async Task RegisterClient_returns_null_on_non_success_status()
    {
        var handler = new RouteHandler();
        handler.PostStatus("https://auth.example.com/register", HttpStatusCode.BadRequest);

        using var http = new HttpClient(handler);
        var client = new McpAuthMetadataClient(http);

        Assert.Null(await client.RegisterClientAsync(
            "https://auth.example.com/register", "http://127.0.0.1/cb", ["authorization_code"]));
    }

    private static string AuthDoc() => """
        {"issuer":"https://auth.example.com",
         "authorization_endpoint":"https://auth.example.com/authorize",
         "token_endpoint":"https://auth.example.com/token",
         "registration_endpoint":"https://auth.example.com/register"}
        """;

    /// <summary>A stub handler that routes by method+URL and records what it was asked for.</summary>
    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpStatusCode> getStatus = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> getBody = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HttpStatusCode> postStatus = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> postBody = new(StringComparer.Ordinal);

        public bool ThrowOnEveryRequest { get; init; }
        public List<string> SeenUrls { get; } = [];
        public string? LastPostBody { get; private set; }

        public void Get(string url, string json)
        {
            this.getStatus[url] = HttpStatusCode.OK;
            this.getBody[url] = json;
        }

        public void Post(string url, string json)
        {
            this.postStatus[url] = HttpStatusCode.OK;
            this.postBody[url] = json;
        }

        public void PostStatus(string url, HttpStatusCode status)
        {
            this.postStatus[url] = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (this.ThrowOnEveryRequest)
            {
                throw new HttpRequestException("connection refused");
            }

            var url = request.RequestUri!.ToString();
            this.SeenUrls.Add(url);

            if (request.Method == HttpMethod.Post)
            {
                this.LastPostBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);

                if (this.postStatus.TryGetValue(url, out var ps) && ps == HttpStatusCode.OK)
                {
                    return Ok(this.postBody[url]);
                }

                return new HttpResponseMessage(this.postStatus.TryGetValue(url, out var s) ? s : HttpStatusCode.NotFound);
            }

            if (this.getStatus.TryGetValue(url, out var gs) && gs == HttpStatusCode.OK)
            {
                return Ok(this.getBody[url]);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

/// <summary>Covers <see cref="McpClientRegistration.Parse"/>.</summary>
public sealed class McpClientRegistrationTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Parse_reads_client_id_and_optional_secret()
    {
        var reg = McpClientRegistration.Parse(Json("""{"client_id":"abc","client_secret":"def"}"""));

        Assert.NotNull(reg);
        Assert.Equal("abc", reg!.ClientId);
        Assert.Equal("def", reg.ClientSecret);
    }

    [Fact]
    public void Parse_allows_a_public_client_without_a_secret()
    {
        var reg = McpClientRegistration.Parse(Json("""{"client_id":"abc"}"""));

        Assert.NotNull(reg);
        Assert.Null(reg!.ClientSecret);
    }

    [Fact]
    public void Parse_returns_null_when_client_id_missing_or_empty()
    {
        Assert.Null(McpClientRegistration.Parse(Json("""{"client_secret":"def"}""")));
        Assert.Null(McpClientRegistration.Parse(Json("""{"client_id":""}""")));
    }
}
