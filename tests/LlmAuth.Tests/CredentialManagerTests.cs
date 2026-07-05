namespace LlmAuth.Tests;

public class CredentialManagerTests
{
    private const string FakeId = "fake";

    private sealed class FakeProvider : ICredentialProvider
    {
        public FakeProvider(string id = FakeId)
        {
            this.ProviderId = id;
        }

        public bool ForceNeedsRefresh { get; set; }

        public Credential? RefreshResult { get; set; }

        public int RefreshCallCount { get; private set; }

        public string ProviderId { get; }

        public ILoginFlow BeginLogin(LoginOptions options) => throw new NotSupportedException();

        public bool NeedsRefresh(Credential credential) => this.ForceNeedsRefresh;

        public Task<Credential> RefreshAsync(Credential credential, CancellationToken cancellationToken = default)
        {
            this.RefreshCallCount++;
            return Task.FromResult(this.RefreshResult ?? credential);
        }

        public AuthHeaders GetAuthHeaders(Credential credential) =>
            new(new Dictionary<string, string> { ["Authorization"] = $"Bearer {credential.AccessToken}" });
    }

    private static Credential SampleCredential(string providerId = FakeId) => new()
    {
        ProviderId = providerId,
        Kind = CredentialKind.OAuth,
        AccessToken = "AT",
        RefreshToken = "RT",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        Scopes = ["user:profile", "user:inference"],
        Account = new AccountInfo { AccountUuid = "acc", EmailAddress = "a@b.com", OrganizationUuid = "org" },
    };

    [Fact]
    public async Task StoreThenGet_RoundTripsThroughJson()
    {
        var provider = new FakeProvider();
        var manager = new CredentialManager(new InMemoryTokenStore(), [provider]);
        var original = SampleCredential();

        await manager.StoreAsync(FakeId, original, default);
        var loaded = await manager.GetCredentialAsync(FakeId, default);

        Assert.NotNull(loaded);
        Assert.Equal(original.Kind, loaded!.Kind);
        Assert.Equal(original.AccessToken, loaded.AccessToken);
        Assert.Equal(original.RefreshToken, loaded.RefreshToken);
        Assert.Equal(original.Scopes, loaded.Scopes);
        Assert.Equal(original.Account, loaded.Account);
        Assert.NotNull(loaded.ExpiresAt);
        Assert.Equal(original.ExpiresAt!.Value.ToUnixTimeSeconds(), loaded.ExpiresAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task GetCredential_WhenNeedsRefresh_RefreshesAndPersists()
    {
        var refreshed = SampleCredential() with { AccessToken = "AT2", RefreshToken = "RT2" };
        var provider = new FakeProvider { ForceNeedsRefresh = true, RefreshResult = refreshed };
        var store = new InMemoryTokenStore();
        var manager = new CredentialManager(store, [provider]);

        await manager.StoreAsync(FakeId, SampleCredential(), default);
        var result = await manager.GetCredentialAsync(FakeId, default);

        Assert.Equal("AT2", result!.AccessToken);
        Assert.Equal(1, provider.RefreshCallCount);

        // Persisted: a second read without refresh (ForceNeedsRefresh keeps refreshing though,
        // so verify the store now holds the refreshed token).
        provider.ForceNeedsRefresh = false;
        var second = await manager.GetCredentialAsync(FakeId, default);
        Assert.Equal("AT2", second!.AccessToken);
    }

    [Fact]
    public async Task GetAuthHeaders_NothingStored_ThrowsCredentialNotFound()
    {
        var manager = new CredentialManager(new InMemoryTokenStore(), [new FakeProvider()]);
        await Assert.ThrowsAsync<CredentialNotFoundException>(() => manager.GetAuthHeadersAsync(FakeId, default));
    }

    [Fact]
    public async Task UnknownProvider_ThrowsProviderNotRegistered()
    {
        var manager = new CredentialManager(new InMemoryTokenStore(), [new FakeProvider()]);
        await Assert.ThrowsAsync<ProviderNotRegisteredException>(
            () => manager.GetCredentialAsync("nope", default));
    }

    [Fact]
    public async Task Logout_RemovesCredential()
    {
        var manager = new CredentialManager(new InMemoryTokenStore(), [new FakeProvider()]);
        await manager.StoreAsync(FakeId, SampleCredential(), default);
        await manager.LogoutAsync(FakeId, default);

        Assert.Null(await manager.GetCredentialAsync(FakeId, default));
    }

    [Fact]
    public void ProviderIds_ReflectsRegistered()
    {
        var manager = new CredentialManager(new InMemoryTokenStore(), [new FakeProvider()]);
        Assert.Contains(FakeId, manager.ProviderIds);
    }

    [Fact]
    public async Task Store_SecondProvider_RemovesFirst_SingleCredentialInvariant()
    {
        var store = new InMemoryTokenStore();
        var mgr = new CredentialManager(store, [new FakeProvider("a"), new FakeProvider("b")]);

        await mgr.StoreAsync("a", SampleCredential("a"), default);
        await mgr.StoreAsync("b", SampleCredential("b"), default);

        Assert.Null(await mgr.GetStoredCredentialAsync("a"));
        Assert.NotNull(await mgr.GetStoredCredentialAsync("b"));
        Assert.Equal("b", await mgr.GetConnectedProviderIdAsync());
    }

    [Fact]
    public async Task GetConnectedProviderId_NoCredential_ReturnsNull()
    {
        var mgr = new CredentialManager(new InMemoryTokenStore(), [new FakeProvider("a")]);
        Assert.Null(await mgr.GetConnectedProviderIdAsync());
    }
}
