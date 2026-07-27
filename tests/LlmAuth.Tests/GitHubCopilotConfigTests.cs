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
    public void ForEnterprise_UseExchange_IsTrue(string domain)
    {
        // Enterprise hosts do have a copilot_internal/v2/token exchange endpoint;
        // the raw OAuth token yields fewer models than the exchanged Copilot token.
        var config = GitHubCopilotConfig.ForEnterprise(domain);

        Assert.True(config.UseExchange);
    }

    [Theory]
    [InlineData("microsoft.ghe.com", "https://api.microsoft.ghe.com/copilot_internal/v2/token")]
    [InlineData("https://microsoft.ghe.com", "https://api.microsoft.ghe.com/copilot_internal/v2/token")]
    [InlineData("microsoft.ghe.com/", "https://api.microsoft.ghe.com/copilot_internal/v2/token")]
    public void ForEnterprise_SetsCopilotTokenUrl_ToEnterpriseExchangeEndpoint(string domain, string expected)
    {
        var config = GitHubCopilotConfig.ForEnterprise(domain);

        Assert.Equal(expected, config.CopilotTokenUrl);
    }

    [Theory]
    [InlineData("copilot-api.microsoft.ghe.com")]
    [InlineData("https://copilot-api.microsoft.ghe.com")]
    public void ForEnterprise_CopilotHostPastedByMistake_CopilotTokenUrlIsCorrect(string domain)
    {
        // When the caller pastes the Copilot host instead of the GHE host, the prefix
        // is stripped and all URLs — including CopilotTokenUrl — reference the GHE host.
        var config = GitHubCopilotConfig.ForEnterprise(domain);

        Assert.Equal("https://api.microsoft.ghe.com/copilot_internal/v2/token", config.CopilotTokenUrl);
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

    [Theory]
    [InlineData("acme.com/evil-path")]
    [InlineData("acme.com evil")]
    [InlineData("user@acme.com")]
    [InlineData("acme.com?x=1")]
    [InlineData("acme.com#frag")]
    [InlineData("acme.com\\evil")]
    public void ForEnterprise_DomainWithPathQueryFragmentOrUserInfo_Throws(string domain)
    {
        // The domain is interpolated into api.<domain> and used as the destination for the
        // durable OAuth token exchange; a stray path/query/fragment/userinfo must not silently
        // redirect that token to an unintended host.
        Assert.Throws<ArgumentException>(() => GitHubCopilotConfig.ForEnterprise(domain));
    }

    [Theory]
    [InlineData("copilot-api.microsoft.ghe.com")]
    [InlineData("https://copilot-api.microsoft.ghe.com")]
    public void ForEnterprise_CopilotHostPastedByMistake_RecoversGheHost(string domain)
    {
        // If the user pastes the Copilot host instead of the GHE host, recover the GHE host so
        // every derived URL is consistent and we never double the "copilot-api." prefix.
        var config = GitHubCopilotConfig.ForEnterprise(domain);

        Assert.Equal("https://microsoft.ghe.com/login/device/code", config.DeviceCodeUrl);
        Assert.Equal("https://microsoft.ghe.com/login/oauth/access_token", config.TokenUrl);
        Assert.Equal("https://copilot-api.microsoft.ghe.com", config.ApiBaseUrl);
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
            Assert.Equal("https://api.contoso.ghe.com/copilot_internal/v2/token", config.CopilotTokenUrl);
            Assert.True(config.UseExchange);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_COPILOT_ENTERPRISE_DOMAIN", null);
        }
    }

    [Fact]
    public void FromEnvironment_EnterpriseDomain_UseExchangeCanBeOverriddenToFalse()
    {
        // GH_COPILOT_USE_EXCHANGE=false must win over the ForEnterprise default (true).
        ClearCopilotEnv();
        Environment.SetEnvironmentVariable("GH_COPILOT_ENTERPRISE_DOMAIN", "contoso.ghe.com");
        Environment.SetEnvironmentVariable("GH_COPILOT_USE_EXCHANGE", "false");
        try
        {
            var config = GitHubCopilotConfig.FromEnvironment();
            Assert.False(config.UseExchange);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_COPILOT_ENTERPRISE_DOMAIN", null);
            Environment.SetEnvironmentVariable("GH_COPILOT_USE_EXCHANGE", null);
        }
    }

    [Fact]
    public void FromEnvironment_EnterpriseDomain_CopilotTokenUrlCanBeOverridden()
    {
        // GH_COPILOT_COPILOT_TOKEN_URL must win over the ForEnterprise-derived value.
        ClearCopilotEnv();
        Environment.SetEnvironmentVariable("GH_COPILOT_ENTERPRISE_DOMAIN", "contoso.ghe.com");
        Environment.SetEnvironmentVariable("GH_COPILOT_COPILOT_TOKEN_URL", "https://custom-token.contoso.internal/copilot/v2/token");
        try
        {
            var config = GitHubCopilotConfig.FromEnvironment();
            Assert.Equal("https://custom-token.contoso.internal/copilot/v2/token", config.CopilotTokenUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_COPILOT_ENTERPRISE_DOMAIN", null);
            Environment.SetEnvironmentVariable("GH_COPILOT_COPILOT_TOKEN_URL", null);
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
            // UseExchange inherits the ForEnterprise default (true)
            Assert.True(config.UseExchange);
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

        // UseExchange=false: explicit opt-out for hosts that accept the raw token directly.
        var config = GitHubCopilotConfig.ForEnterprise("microsoft.ghe.com") with { UseExchange = false };
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
    public void NeedsRefresh_NoExpiresAt_NonRawToken_ReturnsFalse()
    {
        // An already-exchanged-looking token (not a raw GitHub OAuth prefix) with no
        // ExpiresAt is the durable/long-lived case and must not trigger a refresh. This is
        // deliberately NOT a "ghe_"-prefixed token: that scenario is the raw-token self-heal
        // covered by NeedsRefresh_EnterpriseConfig_RawGheToken_SelfHeals_ReturnsTrue below.
        var config = GitHubCopilotConfig.ForEnterprise("microsoft.ghe.com");
        using var provider = new GitHubCopilotProvider(config, new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}"))));
        var credential = new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "tid=already-exchanged-enterprise-token",
            RefreshToken = "ghu_underlying",
            ExpiresAt = null,
        };

        Assert.False(provider.NeedsRefresh(credential));
    }

    [Fact]
    public void NeedsRefresh_EnterpriseConfig_RawGheToken_SelfHeals_ReturnsTrue()
    {
        // This is exactly the scenario the self-heal exists for: a credential stored before
        // the Enterprise exchange fix, still carrying the raw "ghe_" device-flow token, under
        // a config that now expects the exchanged Copilot token (UseExchange=true).
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

        Assert.True(provider.NeedsRefresh(credential));
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
