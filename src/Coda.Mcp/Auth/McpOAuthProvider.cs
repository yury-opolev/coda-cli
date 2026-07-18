using System.Text.Json;
using LlmAuth;

namespace Coda.Mcp.Auth;

/// <summary>
/// Implements the MCP authorization flow (OAuth 2.1 + PKCE) for a single HTTP server,
/// reusing the <see cref="LlmAuth"/> primitives. On a 401 it performs RFC 9728 →
/// RFC 8414/OIDC discovery, obtains a client id (configured, cached, or via RFC 7591
/// Dynamic Client Registration), runs an authorization-code + PKCE flow with the RFC 8707
/// <c>resource</c> parameter and RFC 9207 <c>iss</c> validation, then persists the token
/// (keyed by canonical resource URI) and refreshes it as needed.
/// </summary>
public sealed class McpOAuthProvider : IMcpAuthProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;
    private readonly McpAuthMetadataClient metadata;
    private readonly ITokenStore tokenStore;
    private readonly string canonicalResource;
    private readonly McpAuthConfig config;
    private readonly bool interactive;
    private readonly Func<Uri, CancellationToken, Task> openBrowser;
    private readonly Func<LoopbackRedirectListener> listenerFactory;
    private readonly Action<string>? log;

    public McpOAuthProvider(
        HttpClient http,
        ITokenStore tokenStore,
        string canonicalResource,
        McpAuthConfig config,
        bool interactive,
        Func<Uri, CancellationToken, Task>? openBrowser = null,
        Func<LoopbackRedirectListener>? listenerFactory = null,
        Action<string>? log = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.metadata = new McpAuthMetadataClient(http);
        this.tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        this.canonicalResource = canonicalResource ?? throw new ArgumentNullException(nameof(canonicalResource));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.interactive = interactive;
        this.openBrowser = openBrowser ?? SystemBrowser.OpenAsync;
        this.listenerFactory = listenerFactory ?? (() => new LoopbackRedirectListener());
        this.log = log;
    }

    private string TokenKey => $"mcp-token:{this.canonicalResource}";

    private static string ClientKey(string issuer) => $"mcp-client:{issuer}";

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var stored = await this.LoadTokenAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        if (!stored.IsExpired(DateTimeOffset.UtcNow))
        {
            return stored.AccessToken;
        }

        if (!string.IsNullOrEmpty(stored.RefreshToken))
        {
            var refreshed = await this.RefreshAsync(stored, cancellationToken).ConfigureAwait(false);
            return refreshed?.AccessToken;
        }

        return null;
    }

    public async Task<bool> HandleUnauthorizedAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        var challenge = WwwAuthenticateChallenge.Parse(response.Headers.WwwAuthenticate.FirstOrDefault()?.ToString());

        if (!this.interactive)
        {
            this.log?.Invoke(
                $"MCP server requires authorization. Run `coda` interactively to sign in to {this.canonicalResource}.");
            return false;
        }

        try
        {
            return await this.AcquireTokenAsync(challenge, cancellationToken).ConfigureAwait(false);
        }
        catch (LoginCanceledException)
        {
            this.log?.Invoke($"Authorization canceled for {this.canonicalResource}.");
            return false;
        }
        catch (LlmAuthException ex)
        {
            this.log?.Invoke($"Authorization failed for {this.canonicalResource}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> AcquireTokenAsync(WwwAuthenticateChallenge? challenge, CancellationToken cancellationToken)
    {
        // 1. Resource metadata: from the challenge, else the well-known origin path.
        var metadataUrl = ResolveResourceMetadataUrl(challenge, this.canonicalResource);
        var prm = await this.metadata.GetProtectedResourceMetadataAsync(metadataUrl, cancellationToken).ConfigureAwait(false);

        var issuerCandidate = prm?.AuthorizationServers.FirstOrDefault();
        if (string.IsNullOrEmpty(issuerCandidate))
        {
            this.log?.Invoke($"No authorization server advertised by {this.canonicalResource}.");
            return false;
        }

        // 2. Authorization-server metadata (RFC 8414, then OIDC).
        var asMeta = await this.metadata.GetAuthorizationServerMetadataAsync(issuerCandidate, cancellationToken).ConfigureAwait(false);
        if (asMeta is null)
        {
            this.log?.Invoke($"Could not discover authorization-server metadata for {issuerCandidate}.");
            return false;
        }

        // 3. Loopback listener first, so DCR can register the exact redirect URI.
        using var listener = this.listenerFactory();
        var redirectUri = listener.RedirectUri;

        var resolution = await this.ResolveClientIdAsync(asMeta, redirectUri, cancellationToken).ConfigureAwait(false);
        var clientId = resolution.ClientId;
        if (string.IsNullOrEmpty(clientId))
        {
            // A well-formed resolution always carries an actionable Error here; the fallback
            // guards only against a malformed result (neither ClientId nor Error).
            this.log?.Invoke(resolution.Error
                ?? $"No client id available for {asMeta.Issuer}. Configure auth.clientId or use an authenticated stdio proxy.");
            return false;
        }

        // 4. Scope selection per the spec priority order.
        var scopes = SelectScopes(challenge, this.config, prm, asMeta);

        // 5. PKCE + authorize.
        var verifier = Pkce.GenerateCodeVerifier();
        var codeChallenge = Pkce.GenerateCodeChallenge(verifier);
        var state = Pkce.GenerateState();

        var authorizeUrl = OAuth2PkceClient.BuildAuthorizeUrl(asMeta.AuthorizationEndpoint,
        [
            new("response_type", "code"),
            new("client_id", clientId),
            new("redirect_uri", redirectUri),
            new("code_challenge", codeChallenge),
            new("code_challenge_method", "S256"),
            new("state", state),
            new("scope", scopes.Count > 0 ? string.Join(' ', scopes) : null),
            new("resource", this.canonicalResource),
        ]);

        this.log?.Invoke($"Opening browser to authorize MCP server {this.canonicalResource}…");
        await this.openBrowser(authorizeUrl, cancellationToken).ConfigureAwait(false);

        var redirect = await listener.WaitForCallbackAsync(cancellationToken).ConfigureAwait(false);
        ValidateRedirect(redirect, state, asMeta);

        // 6. Exchange the code (form-encoded) including the resource parameter.
        var token = await this.TokenRequestAsync(asMeta.TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = redirect.Code!,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
            ["resource"] = this.canonicalResource,
        }, cancellationToken).ConfigureAwait(false);

        if (token?.AccessToken is null)
        {
            this.log?.Invoke($"Token exchange returned no access token for {this.canonicalResource}.");
            return false;
        }

        await this.PersistAsync(token, asMeta.Issuer, clientId, string.Join(' ', scopes), cancellationToken).ConfigureAwait(false);
        this.log?.Invoke($"Authorized MCP server {this.canonicalResource}.");
        return true;
    }

    internal async Task<McpClientIdResolution> ResolveClientIdAsync(AuthorizationServerMetadata asMeta, string redirectUri, CancellationToken cancellationToken)
    {
        // Priority: configured client id → cached DCR registration → fresh DCR.
        if (!string.IsNullOrEmpty(this.config.ClientId))
        {
            return McpClientIdResolution.Success(this.config.ClientId);
        }

        var cachedJson = await this.tokenStore.GetAsync(ClientKey(asMeta.Issuer), cancellationToken).ConfigureAwait(false);
        if (cachedJson is not null)
        {
            var cached = Deserialize<McpClientRegistration>(cachedJson);
            if (cached is not null && !string.IsNullOrEmpty(cached.ClientId))
            {
                return McpClientIdResolution.Success(cached.ClientId);
            }
        }

        if (string.IsNullOrEmpty(asMeta.RegistrationEndpoint))
        {
            return McpClientIdResolution.Failure(
                $"Authorization server {asMeta.Issuer} does not advertise dynamic client registration. "
                + "Configure auth.clientId or use an authenticated stdio proxy.");
        }

        var registration = await this.metadata.RegisterClientAsync(
            asMeta.RegistrationEndpoint,
            redirectUri,
            ["authorization_code", "refresh_token"],
            cancellationToken).ConfigureAwait(false);

        if (registration is null || string.IsNullOrEmpty(registration.ClientId))
        {
            return McpClientIdResolution.Failure(
                $"Dynamic client registration failed at {asMeta.RegistrationEndpoint}. "
                + "Configure auth.clientId or use an authenticated stdio proxy.");
        }

        await this.tokenStore.SetAsync(ClientKey(asMeta.Issuer), Serialize(registration), cancellationToken).ConfigureAwait(false);
        return McpClientIdResolution.Success(registration.ClientId);
    }

    private async Task<McpStoredToken?> RefreshAsync(McpStoredToken stored, CancellationToken cancellationToken)
    {
        var asMeta = await this.metadata.GetAuthorizationServerMetadataAsync(stored.Issuer, cancellationToken).ConfigureAwait(false);
        if (asMeta is null)
        {
            return null;
        }

        OAuthTokenResponse? token;
        try
        {
            token = await this.TokenRequestAsync(asMeta.TokenEndpoint, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = stored.RefreshToken!,
                ["client_id"] = stored.ClientId,
                ["resource"] = this.canonicalResource,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthExchangeException)
        {
            // Refresh rejected — drop the token so the next request re-authorizes.
            await this.tokenStore.DeleteAsync(this.TokenKey, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (token?.AccessToken is null)
        {
            return null;
        }

        // A rotated refresh token replaces the old one; otherwise keep the existing one.
        var refreshToken = token.RefreshToken ?? stored.RefreshToken;
        var updated = await this.PersistAsync(token, stored.Issuer, stored.ClientId, stored.Scope, cancellationToken, refreshToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<OAuthTokenResponse?> TokenRequestAsync(string tokenEndpoint, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthExchangeException((int)response.StatusCode, body);
        }

        return JsonSerializer.Deserialize<OAuthTokenResponse>(body, JsonOptions);
    }

    private async Task<McpStoredToken> PersistAsync(
        OAuthTokenResponse token,
        string issuer,
        string clientId,
        string scope,
        CancellationToken cancellationToken,
        string? refreshTokenOverride = null)
    {
        var expiresAt = token.ExpiresIn is > 0
            ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + token.ExpiresIn.Value
            : 0;

        var stored = new McpStoredToken(
            token.AccessToken!,
            refreshTokenOverride ?? token.RefreshToken,
            expiresAt,
            string.IsNullOrEmpty(token.Scope) ? scope : token.Scope!,
            issuer,
            clientId);

        await this.tokenStore.SetAsync(this.TokenKey, Serialize(stored), cancellationToken).ConfigureAwait(false);
        return stored;
    }

    private async Task<McpStoredToken?> LoadTokenAsync(CancellationToken cancellationToken)
    {
        var json = await this.tokenStore.GetAsync(this.TokenKey, cancellationToken).ConfigureAwait(false);
        return json is null ? null : Deserialize<McpStoredToken>(json);
    }

    internal static Uri ResolveResourceMetadataUrl(WwwAuthenticateChallenge? challenge, string canonicalResource)
    {
        if (!string.IsNullOrEmpty(challenge?.ResourceMetadata)
            && Uri.TryCreate(challenge.ResourceMetadata, UriKind.Absolute, out var fromChallenge))
        {
            return fromChallenge;
        }

        var origin = new Uri(canonicalResource);
        return new Uri($"{origin.Scheme}://{origin.Authority}/.well-known/oauth-protected-resource");
    }

    internal static IReadOnlyList<string> SelectScopes(
        WwwAuthenticateChallenge? challenge,
        McpAuthConfig config,
        ProtectedResourceMetadata? prm,
        AuthorizationServerMetadata asMeta)
    {
        IReadOnlyList<string> scopes;
        if (!string.IsNullOrWhiteSpace(challenge?.Scope))
        {
            scopes = challenge.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else if (config.Scopes is { Count: > 0 })
        {
            scopes = config.Scopes;
        }
        else if (prm?.ScopesSupported is { Count: > 0 })
        {
            scopes = prm.ScopesSupported;
        }
        else
        {
            scopes = asMeta.ScopesSupported;
        }

        // Request a refresh token when the AS advertises offline_access.
        if (asMeta.ScopesSupported.Contains("offline_access") && !scopes.Contains("offline_access"))
        {
            scopes = [.. scopes, "offline_access"];
        }

        return scopes;
    }

    private static void ValidateRedirect(RedirectResult redirect, string expectedState, AuthorizationServerMetadata asMeta)
    {
        if (!string.IsNullOrEmpty(redirect.Error))
        {
            throw new LlmAuthException($"Authorization server returned an error: {redirect.Error}");
        }

        if (!string.Equals(redirect.State, expectedState, StringComparison.Ordinal))
        {
            throw new LlmAuthException("OAuth state mismatch (possible CSRF); aborting login.");
        }

        // RFC 9207 issuer validation. A present iss must match; when the AS advertises
        // support, an absent iss is rejected.
        if (!string.IsNullOrEmpty(redirect.Iss))
        {
            if (!string.Equals(redirect.Iss, asMeta.Issuer, StringComparison.Ordinal))
            {
                throw new LlmAuthException("OAuth issuer (iss) mismatch; aborting login.");
            }
        }
        else if (asMeta.IssuerParameterSupported)
        {
            throw new LlmAuthException("Authorization response missing required iss parameter; aborting login.");
        }

        if (string.IsNullOrEmpty(redirect.Code))
        {
            throw new LlmAuthException("Authorization response did not include a code.");
        }
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
