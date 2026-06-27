namespace LlmAuth.Providers.ClaudeAi;

/// <summary>
/// Static API-key provider: reads a caller-supplied key or
/// <c>ANTHROPIC_API_KEY</c> and sends <c>x-api-key</c> (matching the non-OAuth
/// path of <c>getAuthHeaders()</c>). No interactive login or refresh.
/// </summary>
public sealed class ApiKeyProvider : ICredentialProvider
{
    public const string Id = "anthropic-api-key";

    /// <summary>Environment variable read when no key is passed in.</summary>
    public const string EnvVarName = "ANTHROPIC_API_KEY";

    private readonly Func<string?> keyResolver;

    public ApiKeyProvider(string? apiKey = null)
    {
        this.keyResolver = () => apiKey ?? Environment.GetEnvironmentVariable(EnvVarName);
    }

    public string ProviderId => Id;

    public ILoginFlow BeginLogin(LoginOptions options)
    {
        throw new NotSupportedException("The API-key provider has no interactive login; supply the key directly.");
    }

    public bool NeedsRefresh(Credential credential) => false;

    public Task<Credential> RefreshAsync(Credential credential, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(credential);
    }

    public AuthHeaders GetAuthHeaders(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var key = credential.ApiKey
            ?? this.keyResolver()
            ?? throw new CredentialNotFoundException("No API key available (set ANTHROPIC_API_KEY or pass one in).");

        return new AuthHeaders(new Dictionary<string, string> { ["x-api-key"] = key });
    }

    /// <summary>Build a credential from the resolved key (for storing/registering).</summary>
    public Credential CreateCredential()
    {
        var key = this.keyResolver()
            ?? throw new CredentialNotFoundException("No API key available (set ANTHROPIC_API_KEY or pass one in).");
        return new Credential { ProviderId = Id, Kind = CredentialKind.ApiKey, ApiKey = key };
    }
}
