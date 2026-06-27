namespace LlmAuth;

/// <summary>
/// A pluggable auth strategy for one provider (Claude.ai, an API key, later
/// GitHub Copilot / OpenAI). Producing the <see cref="AuthHeaders"/> and driving
/// the interactive login both live here so providers with very different flows
/// (browser PKCE vs device-code vs static key) share one contract.
/// </summary>
public interface ICredentialProvider
{
    /// <summary>Stable provider id, also used as the token-store key suffix.</summary>
    string ProviderId { get; }

    /// <summary>Begin an interactive login, returning the in-progress flow.</summary>
    ILoginFlow BeginLogin(LoginOptions options);

    /// <summary>True if the credential should be refreshed before use.</summary>
    bool NeedsRefresh(Credential credential);

    /// <summary>Refresh and return an updated credential.</summary>
    Task<Credential> RefreshAsync(Credential credential, CancellationToken cancellationToken = default);

    /// <summary>The auth headers this credential contributes to a provider request.</summary>
    AuthHeaders GetAuthHeaders(Credential credential);
}
