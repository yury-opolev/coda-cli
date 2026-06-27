using System.Net;
using System.Text.Json;
using LlmAuth.Providers.ClaudeAi;

namespace LlmAuth.Tests;

public class TokenExchangeTests
{
    private const string ExchangeResponse =
        """
        {"access_token":"AT","refresh_token":"RT","expires_in":3600,"scope":"user:profile user:inference","account":{"uuid":"acc","email_address":"a@b.com"},"organization":{"uuid":"org"}}
        """;

    [Fact]
    public async Task Exchange_SendsExpectedRequestAndMapsResponse()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.OK, ExchangeResponse));
        using var provider = new ClaudeAiProvider(ClaudeAiOAuthConfig.Prod, new HttpClient(stub));

        var flow = provider.BeginLogin(new LoginOptions { RedirectMode = RedirectMode.Manual });
        var before = DateTimeOffset.UtcNow;
        var cred = await flow.CompleteAsync("AUTHCODE", flow.State, default);
        var after = DateTimeOffset.UtcNow;

        // Request shape.
        Assert.Equal(HttpMethod.Post, stub.LastMethod);
        Assert.Equal("https://platform.claude.com/v1/oauth/token", stub.LastUri!.AbsoluteUri);
        Assert.Equal("application/json", stub.LastContentType);

        using var doc = JsonDocument.Parse(stub.LastBody!);
        var root = doc.RootElement;
        Assert.Equal("authorization_code", root.GetProperty("grant_type").GetString());
        Assert.Equal("AUTHCODE", root.GetProperty("code").GetString());
        Assert.Equal("https://platform.claude.com/oauth/code/callback", root.GetProperty("redirect_uri").GetString());
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", root.GetProperty("client_id").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("code_verifier").GetString()));
        Assert.Equal(flow.State, root.GetProperty("state").GetString());

        // Mapped credential.
        Assert.Equal("AT", cred.AccessToken);
        Assert.Equal("RT", cred.RefreshToken);
        Assert.Equal(CredentialKind.OAuth, cred.Kind);
        Assert.Equal(ClaudeAiProvider.Id, cred.ProviderId);
        Assert.Equal(new[] { "user:profile", "user:inference" }, cred.Scopes);
        Assert.Equal("acc", cred.Account!.AccountUuid);
        Assert.Equal("a@b.com", cred.Account!.EmailAddress);
        Assert.Equal("org", cred.Account!.OrganizationUuid);

        var expected = before.AddSeconds(3600);
        Assert.NotNull(cred.ExpiresAt);
        Assert.InRange(cred.ExpiresAt!.Value, expected.AddSeconds(-5), after.AddSeconds(3600).AddSeconds(5));
    }

    [Fact]
    public async Task Refresh_SendsRefreshGrantWithClaudeAiScopes()
    {
        const string refreshResponse =
            """
            {"access_token":"AT2","refresh_token":"RT2","expires_in":3600,"scope":"user:profile"}
            """;
        var stub = new StubHandler(_ => (HttpStatusCode.OK, refreshResponse));
        using var provider = new ClaudeAiProvider(ClaudeAiOAuthConfig.Prod, new HttpClient(stub));

        var input = new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "old",
            RefreshToken = "OLDRT",
        };

        var refreshed = await provider.RefreshAsync(input, default);

        using var doc = JsonDocument.Parse(stub.LastBody!);
        var root = doc.RootElement;
        Assert.Equal("refresh_token", root.GetProperty("grant_type").GetString());
        Assert.Equal("OLDRT", root.GetProperty("refresh_token").GetString());
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", root.GetProperty("client_id").GetString());
        Assert.Equal(
            "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload",
            root.GetProperty("scope").GetString());

        Assert.Equal("AT2", refreshed.AccessToken);
        Assert.Equal("RT2", refreshed.RefreshToken);
    }

    [Fact]
    public async Task Refresh_WithoutNewRefreshToken_KeepsOldOne()
    {
        const string response =
            """
            {"access_token":"AT2","expires_in":3600,"scope":"user:profile"}
            """;
        var stub = new StubHandler(_ => (HttpStatusCode.OK, response));
        using var provider = new ClaudeAiProvider(ClaudeAiOAuthConfig.Prod, new HttpClient(stub));

        var input = new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            RefreshToken = "OLDRT",
        };

        var refreshed = await provider.RefreshAsync(input, default);
        Assert.Equal("OLDRT", refreshed.RefreshToken);
    }

    [Fact]
    public async Task Exchange_Non200_ThrowsOAuthExchangeException()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.BadRequest, "{\"error\":\"bad\"}"));
        using var provider = new ClaudeAiProvider(ClaudeAiOAuthConfig.Prod, new HttpClient(stub));
        var flow = provider.BeginLogin(new LoginOptions { RedirectMode = RedirectMode.Manual });

        var ex = await Assert.ThrowsAsync<OAuthExchangeException>(
            () => flow.CompleteAsync("AUTHCODE", flow.State, default));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("{\"error\":\"bad\"}", ex.ResponseBody);
    }

    [Fact]
    public async Task Refresh_Non200_ThrowsTokenRefreshException()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.BadRequest, "nope"));
        using var provider = new ClaudeAiProvider(ClaudeAiOAuthConfig.Prod, new HttpClient(stub));

        var input = new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            RefreshToken = "OLDRT",
        };

        await Assert.ThrowsAsync<TokenRefreshException>(() => provider.RefreshAsync(input, default));
    }

    [Fact]
    public async Task CompleteAsync_StateMismatch_Throws()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.OK, ExchangeResponse));
        using var provider = new ClaudeAiProvider(ClaudeAiOAuthConfig.Prod, new HttpClient(stub));
        var flow = provider.BeginLogin(new LoginOptions { RedirectMode = RedirectMode.Manual });

        await Assert.ThrowsAsync<LlmAuthException>(() => flow.CompleteAsync("AUTHCODE", "wrong-state", default));
    }
}
