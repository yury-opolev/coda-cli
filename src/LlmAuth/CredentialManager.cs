using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmAuth;

/// <summary>
/// The façade consumers use: registers providers, drives the high-layer
/// (loopback) login, loads/persists credentials, auto-refreshes on read, and
/// produces the credential auth headers. Per-provider refreshes are coalesced.
/// </summary>
public sealed class CredentialManager
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ITokenStore store;
    private readonly Dictionary<string, ICredentialProvider> providers;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> refreshLocks = new(StringComparer.Ordinal);

    public CredentialManager(ITokenStore store, IEnumerable<ICredentialProvider> providers)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(providers);
        this.providers = providers.ToDictionary(p => p.ProviderId, StringComparer.Ordinal);
    }

    /// <summary>Registered provider ids.</summary>
    public IReadOnlyCollection<string> ProviderIds => this.providers.Keys;

    /// <summary>
    /// Batteries-included interactive login over loopback: allocates a port,
    /// builds the flow, opens the browser (via the host hook or the system
    /// default), waits for the redirect, validates state, exchanges the code,
    /// and persists the credential. Use <c>provider.BeginLogin</c> directly for
    /// the manual/headless low-layer flow.
    /// </summary>
    public async Task<Credential> LoginAsync(
        string providerId,
        LoginOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var provider = this.GetProvider(providerId);
        options ??= new LoginOptions();

        if (options.RedirectMode == RedirectMode.Manual)
        {
            throw new InvalidOperationException(
                "LoginAsync drives the loopback flow. For manual/headless login, call provider.BeginLogin(...) " +
                "and complete it yourself with the pasted code.");
        }

        var port = options.LoopbackPort ?? 0;
        using var listener = new LoopbackRedirectListener(port == 0 ? null : port);
        var effectiveOptions = options with { LoopbackPort = listener.Port };

        var flow = provider.BeginLogin(effectiveOptions);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.LoopbackTimeout);

        var openBrowser = options.OpenBrowser ?? SystemBrowser.OpenAsync;
        await openBrowser(flow.AuthorizeUrl, timeoutCts.Token).ConfigureAwait(false);

        var redirect = await listener.WaitForCallbackAsync(timeoutCts.Token).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(redirect.Error))
        {
            throw new LoginCanceledException($"Authorization failed: {redirect.Error}");
        }

        if (string.IsNullOrEmpty(redirect.Code))
        {
            throw new LoginCanceledException("Redirect did not include an authorization code.");
        }

        if (!string.Equals(redirect.State, flow.State, StringComparison.Ordinal))
        {
            throw new LlmAuthException("OAuth state mismatch (possible CSRF); aborting login.");
        }

        var credential = await flow.CompleteAsync(redirect.Code!, redirect.State!, cancellationToken)
            .ConfigureAwait(false);

        await this.PersistAsync(providerId, credential, cancellationToken).ConfigureAwait(false);
        await this.RemoveOtherCredentialsAsync(providerId, cancellationToken).ConfigureAwait(false);
        return credential;
    }

    /// <summary>
    /// Interactive login via the OAuth Device Authorization Grant (RFC 8628) for
    /// providers that support it (e.g. GitHub Copilot). The host callback
    /// <paramref name="onPrompt"/> receives the user code + verification URL to
    /// display; the provider then polls until the user authorizes. The resulting
    /// credential is persisted.
    /// </summary>
    public async Task<Credential> LoginWithDeviceCodeAsync(
        string providerId,
        Func<DeviceCodePrompt, CancellationToken, Task> onPrompt,
        LoginOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onPrompt);
        var provider = this.GetProvider(providerId);
        if (provider is not IDeviceCodeLoginProvider deviceProvider)
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' does not support device-code login.");
        }

        var credential = await deviceProvider
            .LoginWithDeviceCodeAsync(options ?? new LoginOptions(), onPrompt, cancellationToken)
            .ConfigureAwait(false);

        await this.PersistAsync(providerId, credential, cancellationToken).ConfigureAwait(false);
        await this.RemoveOtherCredentialsAsync(providerId, cancellationToken).ConfigureAwait(false);
        return credential;
    }

    /// <summary>
    /// Load the stored credential, refreshing it first if the provider says it is
    /// near expiry (refreshes are coalesced per provider). Returns null if none stored.
    /// </summary>
    public async Task<Credential?> GetCredentialAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = this.GetProvider(providerId);
        var credential = await this.LoadAsync(providerId, cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return null;
        }

        if (!provider.NeedsRefresh(credential) || credential.RefreshToken is null)
        {
            return credential;
        }

        var gate = this.refreshLocks.GetOrAdd(providerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-read inside the lock: another caller may have refreshed already.
            credential = await this.LoadAsync(providerId, cancellationToken).ConfigureAwait(false) ?? credential;
            if (provider.NeedsRefresh(credential) && credential.RefreshToken is not null)
            {
                credential = await provider.RefreshAsync(credential, cancellationToken).ConfigureAwait(false);
                await this.PersistAsync(providerId, credential, cancellationToken).ConfigureAwait(false);
            }

            return credential;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The credential auth headers for the provider (refreshing if needed).</summary>
    public async Task<AuthHeaders> GetAuthHeadersAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = this.GetProvider(providerId);
        var credential = await this.GetCredentialAsync(providerId, cancellationToken).ConfigureAwait(false)
            ?? throw new CredentialNotFoundException($"No credential stored for provider '{providerId}'. Log in first.");
        return provider.GetAuthHeaders(credential);
    }

    /// <summary>Persist a credential the host obtained itself (e.g. via the low-layer flow).</summary>
    public async Task StoreAsync(string providerId, Credential credential, CancellationToken cancellationToken = default)
    {
        _ = this.GetProvider(providerId);
        await this.PersistAsync(providerId, credential, cancellationToken).ConfigureAwait(false);
        await this.RemoveOtherCredentialsAsync(providerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delete the stored credential for a provider.</summary>
    public Task LogoutAsync(string providerId, CancellationToken cancellationToken = default)
    {
        return this.store.DeleteAsync(StoreKey(providerId), cancellationToken);
    }

    /// <summary>
    /// Load the stored credential for a provider <b>without</b> refreshing it
    /// (no network). Returns null if none is stored. Useful for status displays.
    /// </summary>
    public Task<Credential?> GetStoredCredentialAsync(string providerId, CancellationToken cancellationToken = default)
    {
        _ = this.GetProvider(providerId);
        return this.LoadAsync(providerId, cancellationToken);
    }

    /// <summary>The single provider id that currently has a stored credential, or null.</summary>
    public async Task<string?> GetConnectedProviderIdAsync(CancellationToken cancellationToken = default)
    {
        foreach (var id in this.providers.Keys)
        {
            var raw = await this.store.GetAsync(StoreKey(id), cancellationToken).ConfigureAwait(false);
            if (raw is not null)
            {
                return id;
            }
        }

        return null;
    }

    private async Task<Credential?> LoadAsync(string providerId, CancellationToken cancellationToken)
    {
        var raw = await this.store.GetAsync(StoreKey(providerId), cancellationToken).ConfigureAwait(false);
        return raw is null ? null : JsonSerializer.Deserialize<Credential>(raw, jsonOptions);
    }

    private Task PersistAsync(string providerId, Credential credential, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(credential, jsonOptions);
        return this.store.SetAsync(StoreKey(providerId), json, cancellationToken);
    }

    private ICredentialProvider GetProvider(string providerId)
    {
        return this.providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new ProviderNotRegisteredException(providerId);
    }

    /// <summary>
    /// Delete every stored credential except <paramref name="keepProviderId"/> (pass
    /// <see langword="null"/> to delete ALL stored credentials). Public entry point for
    /// connect paths that don't go through <see cref="StoreAsync"/> or <see cref="LoginAsync"/>
    /// — e.g. the API-key provider has no credential of its own to persist, but "connecting"
    /// to it must still evict any other provider's stored credential to preserve the
    /// single-credential invariant (<c>GetConnectedProviderIdAsync</c> must then return null,
    /// not a stale prior connection).
    /// </summary>
    public Task RemoveAllStoredCredentialsExceptAsync(string? keepProviderId, CancellationToken cancellationToken = default) =>
        this.RemoveOtherCredentialsAsync(keepProviderId, cancellationToken);

    /// <summary>Delete every stored credential except the given provider's (single-credential invariant).</summary>
    private async Task RemoveOtherCredentialsAsync(string? keepProviderId, CancellationToken cancellationToken)
    {
        foreach (var id in this.providers.Keys)
        {
            if (!string.Equals(id, keepProviderId, StringComparison.Ordinal))
            {
                await this.store.DeleteAsync(StoreKey(id), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string StoreKey(string providerId) => $"llmauth:{providerId}";
}
