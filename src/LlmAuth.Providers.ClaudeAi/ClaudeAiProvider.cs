namespace LlmAuth.Providers.ClaudeAi;

/// <summary>
/// Claude.ai subscriber OAuth provider (Authorization Code + PKCE). Produces
/// <c>Authorization: Bearer …</c> + <c>anthropic-beta: oauth-2025-04-20</c>.
/// </summary>
public sealed class ClaudeAiProvider : ICredentialProvider, IDisposable
{
    public const string Id = "claude-ai";

    // isOAuthTokenExpired: refresh when now + 5min >= expiresAt.
    private static readonly TimeSpan refreshBuffer = TimeSpan.FromMinutes(5);

    private readonly ClaudeAiOAuthConfig config;
    private readonly OAuth2PkceClient oauth;
    private readonly HttpClient? ownedHttpClient;

    public ClaudeAiProvider(ClaudeAiOAuthConfig? config = null, HttpClient? httpClient = null)
    {
        this.config = config ?? ClaudeAiOAuthConfig.FromEnvironment();
        if (httpClient is null)
        {
            // 15s timeout matches axios timeout: 15000 in the source.
            this.ownedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            this.oauth = new OAuth2PkceClient(this.ownedHttpClient);
        }
        else
        {
            this.oauth = new OAuth2PkceClient(httpClient);
        }
    }

    public string ProviderId => Id;

    public ILoginFlow BeginLogin(LoginOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var verifier = Pkce.GenerateCodeVerifier();
        var challenge = Pkce.GenerateCodeChallenge(verifier);
        var state = Pkce.GenerateState();

        var manual = options.RedirectMode == RedirectMode.Manual;
        var redirectUri = manual
            ? this.config.ManualRedirectUrl
            : $"http://localhost:{options.LoopbackPort}/callback";

        var authorizeBase = options.UseClaudeAi ? this.config.ClaudeAiAuthorizeUrl : this.config.ConsoleAuthorizeUrl;
        var scopes = options.InferenceOnly
            ? new[] { ClaudeAiOAuthConfig.InferenceScope }
            : [.. this.config.AllScopes];

        // buildAuthUrl: order preserved exactly.
        var query = new List<KeyValuePair<string, string?>>
        {
            new("code", "true"),
            new("client_id", this.config.ClientId),
            new("response_type", "code"),
            new("redirect_uri", redirectUri),
            new("scope", string.Join(" ", scopes)),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
            new("state", state),
        };
        if (options.OrgUuid is not null) { query.Add(new("orgUUID", options.OrgUuid)); }
        if (options.LoginHint is not null) { query.Add(new("login_hint", options.LoginHint)); }
        if (options.LoginMethod is not null) { query.Add(new("login_method", options.LoginMethod)); }

        var url = OAuth2PkceClient.BuildAuthorizeUrl(authorizeBase, query);
        return new ClaudeAiLoginFlow(this, url, state, verifier, redirectUri);
    }

    public bool NeedsRefresh(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return credential.Kind == CredentialKind.OAuth
            && credential.ExpiresAt.HasValue
            && DateTimeOffset.UtcNow + refreshBuffer >= credential.ExpiresAt.Value;
    }

    public async Task<Credential> RefreshAsync(Credential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrEmpty(credential.RefreshToken))
        {
            throw new TokenRefreshException("No refresh token available.");
        }

        // refreshOAuthToken: scope defaults to the full claude.ai set.
        var body = new Dictionary<string, object?>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credential.RefreshToken,
            ["client_id"] = this.config.ClientId,
            ["scope"] = string.Join(" ", ClaudeAiOAuthConfig.ClaudeAiScopes),
        };

        OAuthTokenResponse response;
        try
        {
            response = await this.oauth.PostTokenAsync(this.config.TokenUrl, body, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OAuthExchangeException ex)
        {
            throw new TokenRefreshException($"Token refresh failed (HTTP {ex.StatusCode}).", ex);
        }

        // Source keeps the existing refresh token if the response omits a new one.
        return ToCredential(response, fallbackRefreshToken: credential.RefreshToken);
    }

    public AuthHeaders GetAuthHeaders(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.Kind != CredentialKind.OAuth)
        {
            throw new InvalidOperationException("ClaudeAiProvider handles OAuth credentials only.");
        }

        if (string.IsNullOrEmpty(credential.AccessToken))
        {
            throw new CredentialNotFoundException("OAuth credential has no access token.");
        }

        return new AuthHeaders(new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {credential.AccessToken}",
            ["anthropic-beta"] = ClaudeAiOAuthConfig.OAuthBetaHeader,
        });
    }

    internal async Task<Credential> ExchangeAsync(
        string code,
        string state,
        string verifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        // exchangeCodeForTokens body, order preserved.
        var body = new Dictionary<string, object?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = this.config.ClientId,
            ["code_verifier"] = verifier,
            ["state"] = state,
        };

        var response = await this.oauth.PostTokenAsync(this.config.TokenUrl, body, cancellationToken)
            .ConfigureAwait(false);
        return ToCredential(response);
    }

    private static Credential ToCredential(OAuthTokenResponse response, string? fallbackRefreshToken = null)
    {
        var expiresAt = response.ExpiresIn.HasValue
            ? DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn.Value)
            : (DateTimeOffset?)null;

        var scopes = response.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

        AccountInfo? account = null;
        if (response.Account is not null || response.Organization is not null)
        {
            account = new AccountInfo
            {
                AccountUuid = response.Account?.Uuid,
                EmailAddress = response.Account?.EmailAddress,
                OrganizationUuid = response.Organization?.Uuid,
            };
        }

        return new Credential
        {
            ProviderId = Id,
            Kind = CredentialKind.OAuth,
            AccessToken = response.AccessToken,
            RefreshToken = response.RefreshToken ?? fallbackRefreshToken,
            ExpiresAt = expiresAt,
            Scopes = scopes,
            Account = account,
        };
    }

    public void Dispose()
    {
        this.ownedHttpClient?.Dispose();
    }
}
