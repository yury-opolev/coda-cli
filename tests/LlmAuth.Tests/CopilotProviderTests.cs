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
}
