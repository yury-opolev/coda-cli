using System.Net;
using LlmAuth;
using LlmAuth.Providers.GitHubCopilot;

namespace LlmAuth.Tests;

public sealed class GitHubCopilotConfigTests
{
    // ── ForEnterprise ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("microsoft.ghe.com")]
    [InlineData("https://microsoft.ghe.com")]
    [InlineData("http://microsoft.ghe.com")]
    [InlineData("microsoft.ghe.com/")]
    [InlineData("https://microsoft.ghe.com/")]
    public void ForEnterprise_VariousInputForms_ProducesExactUrls(string domain)
    {
        var config = GitHubCopilotConfig.ForEnterprise(domain);

        Assert.Equal("https://microsoft.ghe.com/login/device/code", config.DeviceCodeUrl);
        Assert.Equal("https://microsoft.ghe.com/login/oauth/access_token", config.TokenUrl);
        Assert.Equal("https://copilot-api.microsoft.ghe.com", config.ApiBaseUrl);
    }

    [Theory]
    [InlineData("microsoft.ghe.com")]
    [InlineData("https://microsoft.ghe.com")]
    [InlineData("http://microsoft.ghe.com")]
    [InlineData("microsoft.ghe.com/")]
    [InlineData("https://microsoft.ghe.com/")]
    public void ForEnterprise_UseExchange_IsFalse(string domain)
    {
        var config = GitHubCopilotConfig.ForEnterprise(domain);

        Assert.False(config.UseExchange);
    }

    [Fact]
    public void ForEnterprise_InheritsClientIdAndEditorHeaders_FromDefault()
    {
        var config = GitHubCopilotConfig.ForEnterprise("microsoft.ghe.com");
        var defaults = GitHubCopilotConfig.Default;

        Assert.Equal(defaults.ClientId, config.ClientId);
        Assert.Equal(defaults.EditorVersion, config.EditorVersion);
        Assert.Equal(defaults.EditorPluginVersion, config.EditorPluginVersion);
        Assert.Equal(defaults.IntegrationId, config.IntegrationId);
        Assert.Equal(defaults.UserAgent, config.UserAgent);
        Assert.Equal(defaults.Scope, config.Scope);
    }

    [Fact]
    public void ForEnterprise_NullOrWhitespaceDomain_Throws()
    {
        Assert.Throws<ArgumentException>(() => GitHubCopilotConfig.ForEnterprise(string.Empty));
        Assert.Throws<ArgumentException>(() => GitHubCopilotConfig.ForEnterprise("   "));
    }

    // ── Default invariant ───────────────────────────────────────────────────────

    [Fact]
    public void Default_UseExchange_IsTrue()
    {
        Assert.True(GitHubCopilotConfig.Default.UseExchange);
    }

    [Fact]
    public void Default_HasPublicGitHubUrls()
    {
        var d = GitHubCopilotConfig.Default;
        Assert.Equal("https://github.com/login/device/code", d.DeviceCodeUrl);
        Assert.Equal("https://github.com/login/oauth/access_token", d.TokenUrl);
        Assert.Equal("https://api.github.com/copilot_internal/v2/token", d.CopilotTokenUrl);
        Assert.Equal("https://api.githubcopilot.com", d.ApiBaseUrl);
    }

    // ── FromEnvironment ─────────────────────────────────────────────────────────

    [Fact]
    public void FromEnvironment_NoEnvVars_MatchesDefault()
    {
        // Ensure none of the relevant vars are set for this process.
        ClearCopilotEnv();

        var config = GitHubCopilotConfig.FromEnvironment();

        Assert.Equal(GitHubCopilotConfig.Default.DeviceCodeUrl, config.DeviceCodeUrl);
        Assert.Equal(GitHubCopilotConfig.Default.TokenUrl, config.TokenUrl);
        Assert.Equal(GitHubCopilotConfig.Default.CopilotTokenUrl, config.CopilotTokenUrl);
        Assert.Equal(GitHubCopilotConfig.Default.ApiBaseUrl, config.ApiBaseUrl);
        Assert.True(config.UseExchange);
    }

    [Fact]
    public void FromEnvironment_EnterpriseDomain_StartsFromForEnterprise()
    {
        ClearCopilotEnv();
        Environment.SetEnvironmentVariable("GH_COPILOT_ENTERPRISE_DOMAIN", "contoso.ghe.com");
        try
        {
            var config = GitHubCopilotConfig.FromEnvironment();

            Assert.Equal("https://contoso.ghe.com/login/device/code", config.DeviceCodeUrl);
            Assert.Equal("https://contoso.ghe.com/login/oauth/access_token", config.TokenUrl);
            Assert.Equal("https://copilot-api.contoso.ghe.com", config.ApiBaseUrl);
            Assert.False(config.UseExchange);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_COPILOT_ENTERPRISE_DOMAIN", null);
        }
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData("0")]
    public void FromEnvironment_UseExchangeFalsy_SetsUseExchangeFalse(string value)
    {
        ClearCopilotEnv();
        Environment.SetEnvironmentVariable("GH_COPILOT_USE_EXCHANGE", value);
        try
        {
            var config = GitHubCopilotConfig.FromEnvironment();
            Assert.False(config.UseExchange);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_COPILOT_USE_EXCHANGE", null);
        }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("1")]
    [InlineData("yes")]
    public void FromEnvironment_UseExchangeTruthy_SetsUseExchangeTrue(string value)
    {
        ClearCopilotEnv();
        Environment.SetEnvironmentVariable("GH_COPILOT_USE_EXCHANGE", value);
        try
        {
            var config = GitHubCopilotConfig.FromEnvironment();
            Assert.True(config.UseExchange);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_COPILOT_USE_EXCHANGE", null);
        }
    }

    [Fact]
    public void FromEnvironment_IndividualUrlOverrides_AppliedOnTopOfEnterprise()
    {
        ClearCopilotEnv();
        Environment.SetEnvironmentVariable("GH_COPILOT_ENTERPRISE_DOMAIN", "acme.ghe.com");
        Environment.SetEnvironmentVariable("GH_COPILOT_API_BASE_URL", "https://custom-api.acme.internal");
        try
        {
            var config = GitHubCopilotConfig.FromEnvironment();

            // Device/token URLs still come from ForEnterprise
            Assert.Equal("https://acme.ghe.com/login/device/code", config.DeviceCodeUrl);
            // ApiBaseUrl overridden individually
            Assert.Equal("https://custom-api.acme.internal", config.ApiBaseUrl);
            Assert.False(config.UseExchange);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_COPILOT_ENTERPRISE_DOMAIN", null);
            Environment.SetEnvironmentVariable("GH_COPILOT_API_BASE_URL", null);
        }
    }

    // ── Provider behavior with UseExchange=false ────────────────────────────────

    [Fact]
    public async Task DeviceLogin_NoExchange_ReturnsDirect_GitHubTokenAsAccessToken()
    {
        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("device/code", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK,
                    """{"device_code":"DC","user_code":"ABCD-1234","verification_uri":"https://ghe.example.com/login/device","expires_in":900,"interval":1}""");
            }

            // oauth/access_token
            return (HttpStatusCode.OK, """{"access_token":"ghe_RAW_OAUTH_TOKEN","token_type":"bearer","scope":"read:user"}""");
        });

        var config = GitHubCopilotConfig.ForEnterprise("microsoft.ghe.com");
        using var provider = new GitHubCopilotProvider(config, new HttpClient(handler));

        var credential = await provider.LoginWithDeviceCodeAsync(
            new LoginOptions(),
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(CredentialKind.OAuth, credential.Kind);
        // Raw GitHub token is used directly as the bearer — no exchange occurred.
        Assert.Equal("ghe_RAW_OAUTH_TOKEN", credential.AccessToken);
        Assert.Equal("ghe_RAW_OAUTH_TOKEN", credential.RefreshToken);
        // ExpiresAt is null so NeedsRefresh returns false (long-lived token).
        Assert.Null(credential.ExpiresAt);
    }

    [Fact]
    public void NeedsRefresh_NoExpiresAt_ReturnsFalse()
    {
        var config = GitHubCopilotConfig.ForEnterprise("microsoft.ghe.com");
        using var provider = new GitHubCopilotProvider(config, new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}"))));
        var credential = new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "ghe_token",
            RefreshToken = "ghe_token",
            ExpiresAt = null,
        };

        Assert.False(provider.NeedsRefresh(credential));
    }

    [Fact]
    public void GetAuthHeaders_IncludesGitHubApiVersionHeader()
    {
        using var provider = new GitHubCopilotProvider(
            GitHubCopilotConfig.Default,
            new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}"))));
        var credential = new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "tid=abc",
        };

        var headers = provider.GetAuthHeaders(credential).Headers;

        Assert.True(headers.ContainsKey("X-GitHub-Api-Version"));
        Assert.Equal("2026-06-01", headers["X-GitHub-Api-Version"]);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static void ClearCopilotEnv()
    {
        foreach (var name in new[]
        {
            "GH_COPILOT_ENTERPRISE_DOMAIN",
            "GH_COPILOT_CLIENT_ID",
            "GH_COPILOT_DEVICE_CODE_URL",
            "GH_COPILOT_TOKEN_URL",
            "GH_COPILOT_COPILOT_TOKEN_URL",
            "GH_COPILOT_API_BASE_URL",
            "GH_COPILOT_USE_EXCHANGE",
            "GH_COPILOT_EDITOR_VERSION",
            "GH_COPILOT_PLUGIN_VERSION",
            "GH_COPILOT_INTEGRATION_ID",
            "GH_COPILOT_USER_AGENT",
        })
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
