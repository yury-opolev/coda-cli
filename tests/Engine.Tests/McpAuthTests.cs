using System.Net;
using System.Text;
using System.Text.Json;
using Coda.Mcp;
using Coda.Mcp.Auth;
using LlmAuth;

namespace Engine.Tests;

public sealed class WwwAuthenticateChallengeTests
{
    [Fact]
    public void Parses_resource_metadata_scope_and_error()
    {
        var challenge = WwwAuthenticateChallenge.Parse(
            """Bearer resource_metadata="https://mcp.example.com/.well-known/oauth-protected-resource", scope="files:read files:write", error="insufficient_scope" """);

        Assert.NotNull(challenge);
        Assert.Equal("https://mcp.example.com/.well-known/oauth-protected-resource", challenge!.ResourceMetadata);
        Assert.Equal("files:read files:write", challenge.Scope);
        Assert.Equal("insufficient_scope", challenge.Error);
    }

    [Fact]
    public void Returns_null_for_non_bearer_or_blank()
    {
        Assert.Null(WwwAuthenticateChallenge.Parse(null));
        Assert.Null(WwwAuthenticateChallenge.Parse("Basic realm=\"x\""));
    }
}

public sealed class CanonicalResourceUriTests
{
    [Theory]
    [InlineData("https://mcp.example.com/mcp", "https://mcp.example.com/mcp")]
    [InlineData("https://mcp.example.com/", "https://mcp.example.com")]
    [InlineData("https://MCP.Example.COM", "https://mcp.example.com")]
    [InlineData("https://mcp.example.com:443/mcp", "https://mcp.example.com/mcp")]
    [InlineData("https://mcp.example.com:8443/mcp", "https://mcp.example.com:8443/mcp")]
    [InlineData("https://mcp.example.com/mcp#frag", "https://mcp.example.com/mcp")]
    public void Normalizes_scheme_host_port_fragment_and_trailing_slash(string input, string expected)
    {
        Assert.Equal(expected, CanonicalResourceUri.From(new Uri(input)));
    }
}

public sealed class McpMetadataParsingTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Protected_resource_metadata_reads_servers_and_scopes()
    {
        var prm = ProtectedResourceMetadata.Parse(Json("""
            {"resource":"https://mcp.example.com","authorization_servers":["https://auth.example.com"],
             "scopes_supported":["files:read","files:write"]}
            """));

        Assert.Equal("https://auth.example.com", Assert.Single(prm.AuthorizationServers));
        Assert.Equal(["files:read", "files:write"], prm.ScopesSupported);
    }

    [Fact]
    public void Authorization_server_metadata_requires_endpoints()
    {
        Assert.Null(AuthorizationServerMetadata.Parse(Json("""{"issuer":"https://a"}""")));

        var meta = AuthorizationServerMetadata.Parse(Json("""
            {"issuer":"https://auth.example.com","authorization_endpoint":"https://auth.example.com/authorize",
             "token_endpoint":"https://auth.example.com/token","registration_endpoint":"https://auth.example.com/register",
             "authorization_response_iss_parameter_supported":true}
            """));

        Assert.NotNull(meta);
        Assert.Equal("https://auth.example.com/token", meta!.TokenEndpoint);
        Assert.Equal("https://auth.example.com/register", meta.RegistrationEndpoint);
        Assert.True(meta.IssuerParameterSupported);
    }
}

public sealed class McpScopeSelectionTests
{
    private static AuthorizationServerMetadata As(params string[] scopes) =>
        new("https://auth", "https://auth/authorize", "https://auth/token", null, scopes, false);

    [Fact]
    public void Prefers_challenge_scope_over_config_and_metadata()
    {
        var scopes = McpOAuthProvider.SelectScopes(
            new WwwAuthenticateChallenge(null, "files:read", null),
            new McpAuthConfig(McpAuthMode.OAuth, Scopes: ["configured"]),
            new ProtectedResourceMetadata(null, [], ["prm"]),
            As());

        Assert.Equal(["files:read"], scopes);
    }

    [Fact]
    public void Falls_back_to_config_then_prm_and_adds_offline_access()
    {
        var fromConfig = McpOAuthProvider.SelectScopes(
            null, new McpAuthConfig(McpAuthMode.OAuth, Scopes: ["cfg"]), null, As());
        Assert.Equal(["cfg"], fromConfig);

        var withOffline = McpOAuthProvider.SelectScopes(
            null, new McpAuthConfig(McpAuthMode.OAuth), new ProtectedResourceMetadata(null, [], ["read"]),
            As("read", "offline_access"));
        Assert.Contains("read", withOffline);
        Assert.Contains("offline_access", withOffline);
    }
}

public sealed class McpResourceMetadataUrlTests
{
    [Fact]
    public void Uses_challenge_url_when_present()
    {
        var url = McpOAuthProvider.ResolveResourceMetadataUrl(
            new WwwAuthenticateChallenge("https://mcp.example.com/.well-known/oauth-protected-resource", null, null),
            "https://mcp.example.com/mcp");

        Assert.Equal("https://mcp.example.com/.well-known/oauth-protected-resource", url.ToString());
    }

    [Fact]
    public void Falls_back_to_well_known_origin()
    {
        var url = McpOAuthProvider.ResolveResourceMetadataUrl(null, "https://mcp.example.com/mcp");
        Assert.Equal("https://mcp.example.com/.well-known/oauth-protected-resource", url.ToString());
    }
}

public sealed class McpOAuthTokenLifecycleTests
{
    [Fact]
    public async Task GetAccessToken_returns_unexpired_stored_token()
    {
        var store = new InMemoryTokenStore();
        var resource = "https://mcp.example.com/mcp";
        var future = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        await store.SetAsync($"mcp-token:{resource}", $$"""
            {"accessToken":"tok-abc","refreshToken":null,"expiresAtUnix":{{future}},"scope":"read","issuer":"https://auth","clientId":"cid"}
            """);

        using var http = new HttpClient();
        var provider = new McpOAuthProvider(http, store, resource, new McpAuthConfig(McpAuthMode.OAuth), interactive: false);

        Assert.Equal("tok-abc", await provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task GetAccessToken_returns_null_when_expired_and_no_refresh()
    {
        var store = new InMemoryTokenStore();
        var resource = "https://mcp.example.com/mcp";
        var past = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10;
        await store.SetAsync($"mcp-token:{resource}", $$"""
            {"accessToken":"old","refreshToken":null,"expiresAtUnix":{{past}},"scope":"read","issuer":"https://auth","clientId":"cid"}
            """);

        using var http = new HttpClient();
        var provider = new McpOAuthProvider(http, store, resource, new McpAuthConfig(McpAuthMode.OAuth), interactive: false);

        Assert.Null(await provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task HandleUnauthorized_in_headless_does_not_prompt()
    {
        using var http = new HttpClient();
        var provider = new McpOAuthProvider(http, new InMemoryTokenStore(), "https://mcp.example.com",
            new McpAuthConfig(McpAuthMode.OAuth), interactive: false);

        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
        Assert.False(await provider.HandleUnauthorizedAsync(response));
    }

    [Fact]
    public async Task GetAccessToken_refreshes_expired_token_using_refresh_grant()
    {
        var handler = new McpAuthRouteHandler();
        handler.Get("https://auth.example.com/.well-known/oauth-authorization-server",
            """{"issuer":"https://auth.example.com","authorization_endpoint":"https://auth.example.com/authorize","token_endpoint":"https://auth.example.com/token"}""");
        handler.Post("https://auth.example.com/token", """{"access_token":"refreshed-token","expires_in":3600}""");

        using var http = new HttpClient(handler);
        var store = new InMemoryTokenStore();
        var resource = "https://mcp.example.com/mcp";
        var past = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10;
        await store.SetAsync($"mcp-token:{resource}", $$"""
            {"accessToken":"old","refreshToken":"refresh-abc","expiresAtUnix":{{past}},"scope":"read","issuer":"https://auth.example.com","clientId":"cid"}
            """);

        var provider = new McpOAuthProvider(http, store, resource, new McpAuthConfig(McpAuthMode.OAuth), interactive: false);

        Assert.Equal("refreshed-token", await provider.GetAccessTokenAsync());
    }
}

/// <summary>
/// Covers <see cref="McpOAuthProvider.ResolveClientIdAsync"/> — the configured / cached /
/// dynamic-registration priority order and its actionable, secret-free failures.
/// </summary>
public sealed class McpClientIdResolutionTests
{
    private const string Issuer = "https://auth.example.com";
    private const string RegistrationEndpoint = "https://auth.example.com/register";
    private const string RedirectUri = "http://localhost:5000/callback";

    private static AuthorizationServerMetadata AsMeta(string? registrationEndpoint) =>
        new(Issuer, "https://auth.example.com/authorize", "https://auth.example.com/token", registrationEndpoint, [], false);

    private static McpOAuthProvider Provider(HttpClient http, ITokenStore store, McpAuthConfig config) =>
        new(http, store, "https://mcp.example.com/mcp", config, interactive: true);

    [Fact]
    public async Task Configured_client_id_wins_over_cached_and_fresh_registration()
    {
        var handler = new McpAuthRouteHandler();
        handler.Post(RegistrationEndpoint, """{"client_id":"fresh-cid"}""");
        using var http = new HttpClient(handler);

        var store = new InMemoryTokenStore();
        await store.SetAsync($"mcp-client:{Issuer}", """{"clientId":"cached-cid","clientSecret":null}""");

        var provider = Provider(http, store, new McpAuthConfig(McpAuthMode.OAuth, ClientId: "configured-cid"));
        var result = await provider.ResolveClientIdAsync(AsMeta(RegistrationEndpoint), RedirectUri, default);

        Assert.Equal("configured-cid", result.ClientId);
        Assert.Null(result.Error);
        // Configured id short-circuits before any cache read or registration POST.
        Assert.Empty(handler.SeenUrls);
    }

    [Fact]
    public async Task Cached_registration_wins_over_fresh_registration()
    {
        var handler = new McpAuthRouteHandler();
        handler.Post(RegistrationEndpoint, """{"client_id":"fresh-cid"}""");
        using var http = new HttpClient(handler);

        var store = new InMemoryTokenStore();
        await store.SetAsync($"mcp-client:{Issuer}", """{"clientId":"cached-cid","clientSecret":null}""");

        var provider = Provider(http, store, new McpAuthConfig(McpAuthMode.OAuth));
        var result = await provider.ResolveClientIdAsync(AsMeta(RegistrationEndpoint), RedirectUri, default);

        Assert.Equal("cached-cid", result.ClientId);
        Assert.Null(result.Error);
        Assert.Equal(0, handler.PostCount);
    }

    [Fact]
    public async Task Missing_registration_endpoint_returns_actionable_failure()
    {
        using var http = new HttpClient(new McpAuthRouteHandler());
        var provider = Provider(http, new InMemoryTokenStore(), new McpAuthConfig(McpAuthMode.OAuth));

        var result = await provider.ResolveClientIdAsync(AsMeta(null), RedirectUri, default);

        Assert.Null(result.ClientId);
        Assert.Equal(
            "Authorization server https://auth.example.com does not advertise dynamic client registration. "
            + "Configure auth.clientId or use an authenticated stdio proxy.",
            result.Error);
    }

    [Fact]
    public async Task Failed_registration_returns_endpoint_specific_failure_without_secret()
    {
        const string sentinel = "sentinel-secret-DO-NOT-LEAK";
        var handler = new McpAuthRouteHandler();
        handler.Post(RegistrationEndpoint, $$"""{"error":"invalid_client","client_secret":"{{sentinel}}"}""", HttpStatusCode.BadRequest);
        using var http = new HttpClient(handler);

        var provider = Provider(http, new InMemoryTokenStore(), new McpAuthConfig(McpAuthMode.OAuth));
        var result = await provider.ResolveClientIdAsync(AsMeta(RegistrationEndpoint), RedirectUri, default);

        Assert.Null(result.ClientId);
        Assert.Equal(
            "Dynamic client registration failed at https://auth.example.com/register. "
            + "Configure auth.clientId or use an authenticated stdio proxy.",
            result.Error);
        Assert.DoesNotContain(sentinel, result.Error);
    }

    [Fact]
    public async Task Successful_registration_is_persisted_and_reused()
    {
        var handler = new McpAuthRouteHandler();
        handler.Post(RegistrationEndpoint, """{"client_id":"fresh-cid","client_secret":"reg-secret"}""");
        using var http = new HttpClient(handler);
        var store = new InMemoryTokenStore();

        var provider = Provider(http, store, new McpAuthConfig(McpAuthMode.OAuth));

        var first = await provider.ResolveClientIdAsync(AsMeta(RegistrationEndpoint), RedirectUri, default);
        Assert.Equal("fresh-cid", first.ClientId);
        Assert.Null(first.Error);
        Assert.Equal(1, handler.PostCount);
        Assert.NotNull(await store.GetAsync($"mcp-client:{Issuer}"));

        // Second resolution reuses the persisted registration without registering again.
        var second = await provider.ResolveClientIdAsync(AsMeta(RegistrationEndpoint), RedirectUri, default);
        Assert.Equal("fresh-cid", second.ClientId);
        Assert.Equal(1, handler.PostCount);
    }
}

/// <summary>
/// Covers the full 401 → discovery → client-id path: when no client id is available the
/// structured resolution failure is logged and the browser is never opened.
/// </summary>
public sealed class McpUnauthorizedResolutionFlowTests
{
    [Fact]
    public async Task Logs_resolution_failure_and_does_not_open_browser_when_no_client_id()
    {
        var handler = new McpAuthRouteHandler();
        handler.Get("https://mcp.example.com/.well-known/oauth-protected-resource",
            """{"resource":"https://mcp.example.com","authorization_servers":["https://auth.example.com"]}""");
        handler.Get("https://auth.example.com/.well-known/oauth-authorization-server",
            """{"issuer":"https://auth.example.com","authorization_endpoint":"https://auth.example.com/authorize","token_endpoint":"https://auth.example.com/token"}""");

        using var http = new HttpClient(handler);
        var logs = new List<string>();
        var browserOpened = false;

        var provider = new McpOAuthProvider(
            http, new InMemoryTokenStore(), "https://mcp.example.com/mcp",
            new McpAuthConfig(McpAuthMode.OAuth), interactive: true,
            openBrowser: (_, _) => { browserOpened = true; return Task.CompletedTask; },
            log: logs.Add);

        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var handled = await provider.HandleUnauthorizedAsync(response);

        Assert.False(handled);
        Assert.False(browserOpened);
        Assert.Contains(logs, m => m ==
            "Authorization server https://auth.example.com does not advertise dynamic client registration. "
            + "Configure auth.clientId or use an authenticated stdio proxy.");
    }
}

/// <summary>A stub handler that routes by method+URL and records what it was asked for.</summary>
internal sealed class McpAuthRouteHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> gets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> posts = new(StringComparer.Ordinal);

    public List<string> SeenUrls { get; } = [];

    public int PostCount { get; private set; }

    public void Get(string url, string json) => this.gets[url] = (HttpStatusCode.OK, json);

    public void Post(string url, string json, HttpStatusCode status = HttpStatusCode.OK) => this.posts[url] = (status, json);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        this.SeenUrls.Add(url);

        if (request.Method == HttpMethod.Post)
        {
            this.PostCount++;
            return Task.FromResult(this.posts.TryGetValue(url, out var p)
                ? Response(p.Status, p.Body)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        return Task.FromResult(this.gets.TryGetValue(url, out var g)
            ? Response(g.Status, g.Body)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
