namespace LlmAuth.Providers.ClaudeAi;

/// <summary>An in-progress Claude.ai OAuth login (holds the PKCE verifier/state).</summary>
internal sealed class ClaudeAiLoginFlow : ILoginFlow
{
    private readonly ClaudeAiProvider provider;
    private readonly string verifier;
    private readonly string redirectUri;

    public ClaudeAiLoginFlow(ClaudeAiProvider provider, Uri authorizeUrl, string state, string verifier, string redirectUri)
    {
        this.provider = provider;
        this.AuthorizeUrl = authorizeUrl;
        this.State = state;
        this.verifier = verifier;
        this.redirectUri = redirectUri;
    }

    public Uri AuthorizeUrl { get; }

    public string State { get; }

    public Task<Credential> CompleteAsync(string code, string state, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        if (!string.Equals(state, this.State, StringComparison.Ordinal))
        {
            throw new LlmAuthException("OAuth state mismatch (possible CSRF); aborting login.");
        }

        return this.provider.ExchangeAsync(code, state, this.verifier, this.redirectUri, cancellationToken);
    }
}
