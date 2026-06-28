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
}
