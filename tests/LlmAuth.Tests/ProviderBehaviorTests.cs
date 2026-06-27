using LlmAuth.Providers.ClaudeAi;

namespace LlmAuth.Tests;

public class ProviderBehaviorTests
{
    private static ClaudeAiProvider NewProvider() =>
        new(ClaudeAiOAuthConfig.Prod, new HttpClient(new StubHandler(_ => (System.Net.HttpStatusCode.OK, "{}"))));

    [Fact]
    public void NeedsRefresh_NearExpiry_True()
    {
        using var provider = NewProvider();
        var cred = new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
        };
        Assert.True(provider.NeedsRefresh(cred));
    }

    [Fact]
    public void NeedsRefresh_FarExpiry_False()
    {
        using var provider = NewProvider();
        var cred = new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        Assert.False(provider.NeedsRefresh(cred));
    }

    [Fact]
    public void NeedsRefresh_ApiKeyKind_False()
    {
        using var provider = NewProvider();
        var cred = new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.ApiKey,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
        };
        Assert.False(provider.NeedsRefresh(cred));
    }

    [Fact]
    public void ClaudeAiGetAuthHeaders_OAuth_ProducesBearerAndBeta()
    {
        using var provider = NewProvider();
        var cred = new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "AT",
        };

        var headers = provider.GetAuthHeaders(cred).Headers;
        Assert.Equal("Bearer AT", headers["Authorization"]);
        Assert.Equal("oauth-2025-04-20", headers["anthropic-beta"]);
    }

    [Fact]
    public void ClaudeAiGetAuthHeaders_WrongKind_Throws()
    {
        using var provider = NewProvider();
        var cred = new Credential { ProviderId = ClaudeAiProvider.Id, Kind = CredentialKind.ApiKey, ApiKey = "k" };
        Assert.Throws<InvalidOperationException>(() => provider.GetAuthHeaders(cred));
    }

    [Fact]
    public void ClaudeAiGetAuthHeaders_NoAccessToken_Throws()
    {
        using var provider = NewProvider();
        var cred = new Credential { ProviderId = ClaudeAiProvider.Id, Kind = CredentialKind.OAuth };
        Assert.Throws<CredentialNotFoundException>(() => provider.GetAuthHeaders(cred));
    }

    [Fact]
    public void ApiKeyProvider_GetAuthHeaders_ProducesXApiKey()
    {
        var provider = new ApiKeyProvider("KEY");
        var cred = provider.CreateCredential();

        Assert.Equal(ApiKeyProvider.Id, cred.ProviderId);
        Assert.Equal(CredentialKind.ApiKey, cred.Kind);
        Assert.Equal("KEY", cred.ApiKey);

        var headers = provider.GetAuthHeaders(cred).Headers;
        Assert.Equal("KEY", headers["x-api-key"]);
    }

    [Fact]
    public void ApiKeyProvider_NeedsRefresh_False()
    {
        var provider = new ApiKeyProvider("KEY");
        Assert.False(provider.NeedsRefresh(provider.CreateCredential()));
    }
}
