using System.Net;
using LlmAuth;
using LlmAuth.Providers.GitHubCopilot;

namespace LlmAuth.Tests;

public sealed class CopilotProviderTests
{
    private static readonly GitHubCopilotConfig Config = GitHubCopilotConfig.Default;

    private static long FutureUnix(int secondsFromNow) =>
        DateTimeOffset.UtcNow.AddSeconds(secondsFromNow).ToUnixTimeSeconds();

    [Fact]
    public async Task DeviceLogin_PendingThenSuccess_ReturnsCopilotCredential()
    {
        var pollCount = 0;
        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("device/code", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK,
                    """{"device_code":"DC","user_code":"WDJB-MJHT","verification_uri":"https://github.com/login/device","expires_in":900,"interval":1}""");
            }

            if (uri.Contains("oauth/access_token", StringComparison.Ordinal))
            {
                pollCount++;
                return pollCount == 1
                    ? (HttpStatusCode.OK, """{"error":"authorization_pending"}""")
                    : (HttpStatusCode.OK, """{"access_token":"gho_TESTTOKEN","token_type":"bearer","scope":"read:user"}""");
            }

            // copilot_internal/v2/token
            return (HttpStatusCode.OK, $$"""{"token":"tid=abc;exp=123","expires_at":{{FutureUnix(1800)}},"refresh_in":1500}""");
        });

        using var provider = new GitHubCopilotProvider(Config, new HttpClient(handler));

        DeviceCodePrompt? shown = null;
        var credential = await provider.LoginWithDeviceCodeAsync(
            new LoginOptions(),
            (prompt, _) => { shown = prompt; return Task.CompletedTask; },
            CancellationToken.None);

        Assert.NotNull(shown);
        Assert.Equal("WDJB-MJHT", shown!.UserCode);
        Assert.Equal("https://github.com/login/device", shown.VerificationUri.ToString());

        Assert.Equal(CredentialKind.OAuth, credential.Kind);
        Assert.Equal("tid=abc;exp=123", credential.AccessToken);
        Assert.Equal("gho_TESTTOKEN", credential.RefreshToken); // durable GitHub token kept
        Assert.NotNull(credential.ExpiresAt);
        Assert.True(pollCount >= 2);
    }

    [Fact]
    public async Task DeviceLogin_AccessDenied_Throws()
    {
        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("device/code", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK,
                    """{"device_code":"DC","user_code":"AAAA-BBBB","verification_uri":"https://github.com/login/device","expires_in":900,"interval":1}""");
            }

            return (HttpStatusCode.OK, """{"error":"access_denied"}""");
        });

        using var provider = new GitHubCopilotProvider(Config, new HttpClient(handler));

        await Assert.ThrowsAsync<LoginCanceledException>(() =>
            provider.LoginWithDeviceCodeAsync(
                new LoginOptions(),
                (_, _) => Task.CompletedTask,
                CancellationToken.None));
    }

    [Fact]
    public async Task RefreshAsync_ExchangesGitHubTokenForNewCopilotToken()
    {
        string? sentAuthorization = null;
        var handler = new StubHandler(request =>
        {
            sentAuthorization = request.Headers.Authorization?.ToString();
            return (HttpStatusCode.OK, $$"""{"token":"tid=fresh","expires_at":{{FutureUnix(1800)}}}""");
        });

        using var provider = new GitHubCopilotProvider(Config, new HttpClient(handler));
        var existing = new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "tid=stale",
            RefreshToken = "gho_TESTTOKEN",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };

        var refreshed = await provider.RefreshAsync(existing, CancellationToken.None);

        Assert.Equal("token gho_TESTTOKEN", sentAuthorization);
        Assert.Equal("tid=fresh", refreshed.AccessToken);
        Assert.Equal("gho_TESTTOKEN", refreshed.RefreshToken);
    }

    [Fact]
    public void GetAuthHeaders_IncludesBearerAndEditorHeaders()
    {
        using var provider = new GitHubCopilotProvider(Config, new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}"))));
        var credential = new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "tid=abc",
        };

        var headers = provider.GetAuthHeaders(credential).Headers;

        Assert.Equal("Bearer tid=abc", headers["Authorization"]);
        Assert.Equal(Config.EditorVersion, headers["Editor-Version"]);
        Assert.Equal(Config.EditorPluginVersion, headers["Editor-Plugin-Version"]);
        Assert.Equal(Config.IntegrationId, headers["Copilot-Integration-Id"]);
        Assert.Equal(Config.UserAgent, headers["User-Agent"]);
    }

    [Theory]
    [InlineData(-1, true)]   // already expired -> refresh
    [InlineData(1, true)]    // within 5-min buffer -> refresh
    [InlineData(60, false)]  // an hour out -> no refresh
    public void NeedsRefresh_RespectsFiveMinuteBuffer(int minutesFromNow, bool expected)
    {
        using var provider = new GitHubCopilotProvider(Config, new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}"))));
        var credential = new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "tid=abc",
            RefreshToken = "gho_x",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(minutesFromNow),
        };

        Assert.Equal(expected, provider.NeedsRefresh(credential));
    }

    [Fact]
    public void BeginLogin_IsNotSupported()
    {
        using var provider = new GitHubCopilotProvider(Config, new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}"))));
        Assert.Throws<NotSupportedException>(() => provider.BeginLogin(new LoginOptions()));
    }

    // ── NeedsRefresh — raw-token self-heal ──────────────────────────────────────

    [Theory]
    [InlineData("ghu_RawDeviceFlowToken")]
    [InlineData("gho_RawOAuthToken")]
    [InlineData("ghp_PersonalAccessToken")]
    [InlineData("ghs_ServerToken")]
    [InlineData("ghr_RunnerToken")]
    [InlineData("ghe_EnterpriseDeviceFlowToken")]
    [InlineData("github_pat_LegacyFineGrainedToken")]
    public void NeedsRefresh_RawGitHubToken_WhenUseExchangeTrue_ReturnsTrue(string rawToken)
    {
        // A stored raw GitHub OAuth token should trigger a refresh so it is exchanged for
        // a proper Copilot token; the exchanged token carries full model entitlement.
        using var provider = new GitHubCopilotProvider(
            Config, // UseExchange=true
            new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}"))));
        var credential = new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = rawToken,
            RefreshToken = rawToken,
            ExpiresAt = null, // null because BuildDirectCredential never sets ExpiresAt
        };

        Assert.True(provider.NeedsRefresh(credential));
    }

    [Theory]
    [InlineData("ghu_RawDeviceFlowToken")]
    [InlineData("gho_RawOAuthToken")]
    public void NeedsRefresh_RawGitHubToken_WhenUseExchangeFalse_ReturnsFalse(string rawToken)
    {
        // When the deployment is configured to use the raw token directly (UseExchange=false),
        // a null ExpiresAt means the credential is long-lived and must not trigger a refresh.
        var directConfig = Config with { UseExchange = false };
        using var provider = new GitHubCopilotProvider(
            directConfig,
            new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}"))));
        var credential = new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = rawToken,
            RefreshToken = rawToken,
            ExpiresAt = null,
        };

        Assert.False(provider.NeedsRefresh(credential));
    }

    [Fact]
    public void NeedsRefresh_ExchangedToken_WithFutureExpiry_ReturnsFalse()
    {
        // A properly exchanged Copilot token with a future expiry must NOT trigger a refresh.
        using var provider = new GitHubCopilotProvider(
            Config,
            new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}"))));
        var credential = new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "tid=abc;exp=123;sku=copilot_enterprise_seat_quota",
            RefreshToken = "gho_underlying_github_token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };

        Assert.False(provider.NeedsRefresh(credential));
    }

    // ── 404 fallback during login and refresh ───────────────────────────────────

    [Fact]
    public async Task DeviceLogin_Exchange404_FallsBackToDirectCredential()
    {
        // If the exchange endpoint returns HTTP 404, login must succeed by falling back to the
        // raw OAuth token as the bearer — failure is better than a hard error at login time.
        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("device/code", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK,
                    """{"device_code":"DC","user_code":"EEEE-9999","verification_uri":"https://github.com/login/device","expires_in":900,"interval":1}""");
            }

            if (uri.Contains("oauth/access_token", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """{"access_token":"ghu_RAWTOKEN","token_type":"bearer","scope":"read:user"}""");
            }

            // Exchange endpoint — absent
            return (HttpStatusCode.NotFound, "{}");
        });

        using var provider = new GitHubCopilotProvider(Config, new HttpClient(handler));

        var credential = await provider.LoginWithDeviceCodeAsync(
            new LoginOptions(),
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(CredentialKind.OAuth, credential.Kind);
        Assert.Equal("ghu_RAWTOKEN", credential.AccessToken);
        Assert.Null(credential.ExpiresAt);
    }

    [Fact]
    public async Task DeviceLogin_Exchange401_Throws()
    {
        // A non-404 error from the exchange endpoint (auth failure, server error) must
        // not be silently downgraded — surface as an exception.
        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("device/code", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK,
                    """{"device_code":"DC","user_code":"FFFF-8888","verification_uri":"https://github.com/login/device","expires_in":900,"interval":1}""");
            }

            if (uri.Contains("oauth/access_token", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """{"access_token":"ghu_RAWTOKEN","token_type":"bearer","scope":"read:user"}""");
            }

            // Exchange endpoint — auth failure
            return (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
        });

        using var provider = new GitHubCopilotProvider(Config, new HttpClient(handler));

        await Assert.ThrowsAsync<TokenRefreshException>(() =>
            provider.LoginWithDeviceCodeAsync(
                new LoginOptions(),
                (_, _) => Task.CompletedTask,
                CancellationToken.None));
    }

    [Fact]
    public async Task RefreshAsync_Exchange404_ReturnsFallbackDirectCredential()
    {
        // When the exchange endpoint returns 404 during refresh, fall back to the raw token
        // so callers receive a usable credential rather than an error.  This causes one extra
        // HTTP request per API call for tenants without an exchange endpoint, which is
        // deliberate: it avoids permanent errors while self-healing automatically if the
        // endpoint is later enabled.
        var handler = new StubHandler(_ => (HttpStatusCode.NotFound, "{}"));
        using var provider = new GitHubCopilotProvider(Config, new HttpClient(handler));

        var existing = new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "ghu_STALE",
            RefreshToken = "ghu_STALE",
            ExpiresAt = null,
        };

        var result = await provider.RefreshAsync(existing, CancellationToken.None);

        Assert.Equal("ghu_STALE", result.AccessToken);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public async Task CredentialManager_DeviceLogin_PersistsCredential()
    {
        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("device/code", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK,
                    """{"device_code":"DC","user_code":"CCCC-DDDD","verification_uri":"https://github.com/login/device","expires_in":900,"interval":1}""");
            }

            if (uri.Contains("oauth/access_token", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """{"access_token":"gho_X"}""");
            }

            return (HttpStatusCode.OK, $$"""{"token":"tid=persisted","expires_at":{{FutureUnix(1800)}}}""");
        });

        using var provider = new GitHubCopilotProvider(Config, new HttpClient(handler));
        var store = new InMemoryTokenStore();
        var manager = new CredentialManager(store, [provider]);

        var credential = await manager.LoginWithDeviceCodeAsync(
            GitHubCopilotProvider.Id,
            (_, _) => Task.CompletedTask,
            cancellationToken: CancellationToken.None);

        Assert.Equal("tid=persisted", credential.AccessToken);

        var reloaded = await manager.GetCredentialAsync(GitHubCopilotProvider.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal("tid=persisted", reloaded!.AccessToken);
    }

    [Fact]
    public async Task CredentialManager_DeviceLogin_OnNonDeviceProvider_Throws()
    {
        var store = new InMemoryTokenStore();
        var manager = new CredentialManager(store, [new ApiKeyOnlyFake()]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.LoginWithDeviceCodeAsync("fake", (_, _) => Task.CompletedTask,
                cancellationToken: CancellationToken.None));
    }

    private sealed class ApiKeyOnlyFake : ICredentialProvider
    {
        public string ProviderId => "fake";

        public ILoginFlow BeginLogin(LoginOptions options) => throw new NotSupportedException();

        public bool NeedsRefresh(Credential credential) => false;

        public Task<Credential> RefreshAsync(Credential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(credential);

        public AuthHeaders GetAuthHeaders(Credential credential) =>
            new(new Dictionary<string, string>());
    }

    // ── Exchange-unavailable classification (transport/5xx/timeout, not just 404) ──────────

    private static Credential RawStaleCredential(string token = "ghu_STALE") => new()
    {
        ProviderId = GitHubCopilotProvider.Id,
        Kind = CredentialKind.OAuth,
        AccessToken = token,
        RefreshToken = token,
        ExpiresAt = null,
    };

    [Fact]
    public async Task RefreshAsync_ExchangeHttpRequestException_FallsBackToDirectCredential()
    {
        // DNS failure / connection refused / TLS mismatch against a host that was never
        // contacted before the exchange existed must not surface as a raw HttpRequestException.
        var handler = new StubHandler(_ => throw new HttpRequestException("simulated transport failure"));
        using var provider = new GitHubCopilotProvider(Config, new HttpClient(handler));

        var result = await provider.RefreshAsync(RawStaleCredential(), CancellationToken.None);

        Assert.Equal("ghu_STALE", result.AccessToken);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public async Task RefreshAsync_ExchangeProbeTimesOut_FallsBackToDirectCredential()
    {
        // Simulates our own bounded probe timeout firing. The caller's token is never
        // canceled, so this must be treated like an absent endpoint, not an unhandled
        // cancellation.
        var handler = new StubHandler(_ => throw new TaskCanceledException("simulated probe timeout"));
        using var provider = new GitHubCopilotProvider(Config, new HttpClient(handler));

        var result = await provider.RefreshAsync(RawStaleCredential(), CancellationToken.None);

        Assert.Equal("ghu_STALE", result.AccessToken);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public async Task RefreshAsync_ExchangeCanceledByCaller_PropagatesCancellation()
    {
        // A genuine user-initiated cancellation must never be downgraded to a fallback.
        // Real HttpClient.SendAsync always surfaces cancellation as TaskCanceledException (a
        // subclass of OperationCanceledException), so assert on the base type via ThrowsAnyAsync
        // rather than requiring the exact subtype.
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var provider = new GitHubCopilotProvider(Config, new HttpClient(handler));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.RefreshAsync(RawStaleCredential(), cts.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task RefreshAsync_ExchangeUnavailableServerError_FallsBackToDirectCredential(HttpStatusCode status)
    {
        // A proxy/wildcard host with nothing behind it (common for 50x on a synthesized
        // domain) is exactly like an absent endpoint.
        var handler = new StubHandler(_ => (status, "{}"));
        using var provider = new GitHubCopilotProvider(Config, new HttpClient(handler));

        var result = await provider.RefreshAsync(RawStaleCredential(), CancellationToken.None);

        Assert.Equal("ghu_STALE", result.AccessToken);
    }

    [Fact]
    public async Task DeviceLogin_ExchangeTransportFailure_FallsBackToDirectCredential()
    {
        // A DNS/connect/TLS failure must not surface as an unhandled HttpRequestException
        // after the user has already authorized in the browser.
        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("device/code", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK,
                    """{"device_code":"DC","user_code":"HHHH-2222","verification_uri":"https://github.com/login/device","expires_in":900,"interval":1}""");
            }

            if (uri.Contains("oauth/access_token", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """{"access_token":"ghu_RAWTOKEN","token_type":"bearer","scope":"read:user"}""");
            }

            throw new HttpRequestException("simulated DNS failure");
        });

        using var provider = new GitHubCopilotProvider(Config, new HttpClient(handler));

        var credential = await provider.LoginWithDeviceCodeAsync(
            new LoginOptions(),
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal("ghu_RAWTOKEN", credential.AccessToken);
        Assert.Null(credential.ExpiresAt);
    }

    // ── Latching the absent endpoint (no re-probe, no re-persist on every call) ────────────

    [Fact]
    public async Task CredentialManager_ExchangeAbsent_ProbesOnlyOnceAndDoesNotRewriteStore()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.NotFound, "{}"));
        using var provider = new GitHubCopilotProvider(Config, new HttpClient(handler));
        var store = new CountingTokenStore();
        var manager = new CredentialManager(store, [provider]);

        await manager.StoreAsync(GitHubCopilotProvider.Id, RawStaleCredential(), CancellationToken.None);
        var setCountAfterStore = store.SetCount;

        var first = await manager.GetCredentialAsync(GitHubCopilotProvider.Id, CancellationToken.None);
        var second = await manager.GetCredentialAsync(GitHubCopilotProvider.Id, CancellationToken.None);

        Assert.Equal("ghu_STALE", first!.AccessToken);
        Assert.Equal("ghu_STALE", second!.AccessToken);

        var exchangeRequests = handler.RequestUris.Count(u => u.AbsoluteUri.Contains("copilot_internal", StringComparison.Ordinal));
        Assert.Equal(1, exchangeRequests);

        // Exactly one extra persist beyond the initial store (the first refresh's fallback);
        // the second GetCredentialAsync call must not touch the store at all.
        Assert.Equal(setCountAfterStore + 1, store.SetCount);
    }

    private sealed class CountingTokenStore : ITokenStore
    {
        private readonly InMemoryTokenStore inner = new();

        public int SetCount { get; private set; }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            this.inner.GetAsync(key, cancellationToken);

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            this.SetCount++;
            return this.inner.SetAsync(key, value, cancellationToken);
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            this.inner.DeleteAsync(key, cancellationToken);
    }

    // ── CopilotTokenUrl validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_InsecureCopilotTokenUrl_ThrowsWithoutSendingRequest()
    {
        // http:// would put the durable OAuth token on the wire in cleartext; fail loudly at
        // first use instead of silently sending it.
        var handler = new StubHandler(_ => (HttpStatusCode.OK, "{}"));
        var insecureConfig = Config with { CopilotTokenUrl = "http://insecure.example.com/copilot_internal/v2/token" };
        using var provider = new GitHubCopilotProvider(insecureConfig, new HttpClient(handler));

        await Assert.ThrowsAsync<LlmAuthException>(
            () => provider.RefreshAsync(RawStaleCredential(), CancellationToken.None));

        Assert.Empty(handler.RequestUris);
    }

    // ── End-to-end: Enterprise config targets the Enterprise exchange endpoint ─────────────

    [Fact]
    public async Task DeviceLogin_EnterpriseConfig_ExchangesAgainstEnterpriseTokenUrl()
    {
        Uri? exchangeUri = null;
        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("device/code", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK,
                    """{"device_code":"DC","user_code":"GGGG-1111","verification_uri":"https://microsoft.ghe.com/login/device","expires_in":900,"interval":1}""");
            }

            if (uri.Contains("oauth/access_token", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """{"access_token":"ghe_RAWTOKEN","token_type":"bearer","scope":"read:user"}""");
            }

            exchangeUri = request.RequestUri;
            return (HttpStatusCode.OK, $$"""{"token":"tid=enterprise","expires_at":{{FutureUnix(1800)}}}""");
        });

        var config = GitHubCopilotConfig.ForEnterprise("microsoft.ghe.com");
        using var provider = new GitHubCopilotProvider(config, new HttpClient(handler));

        var credential = await provider.LoginWithDeviceCodeAsync(
            new LoginOptions(),
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.NotNull(exchangeUri);
        Assert.Equal("https://api.microsoft.ghe.com/copilot_internal/v2/token", exchangeUri!.AbsoluteUri);
        Assert.Equal("tid=enterprise", credential.AccessToken);
    }
}
