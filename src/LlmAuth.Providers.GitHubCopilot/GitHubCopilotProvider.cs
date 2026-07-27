using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace LlmAuth.Providers.GitHubCopilot;

/// <summary>
/// GitHub Copilot credential provider. Logs in via the OAuth Device
/// Authorization Grant (the user types a code at github.com/login/device),
/// obtaining a durable GitHub OAuth token, then exchanges it for a short-lived
/// Copilot token used as the bearer for Copilot API requests. The GitHub token
/// is kept as the "refresh token": <see cref="RefreshAsync"/> re-exchanges it for
/// a fresh Copilot token.
/// </summary>
public sealed class GitHubCopilotProvider : IDeviceCodeLoginProvider, IDisposable
{
    public const string Id = "github-copilot";

    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private static readonly TimeSpan refreshBuffer = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Upper bound on a single Copilot-token-exchange probe, independent of the HttpClient's
    /// own (30s) timeout: on the hot path (login/refresh), a probe against a host that may not
    /// even have the endpoint must not consume the caller's whole request budget.
    /// </summary>
    private static readonly TimeSpan exchangeProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly System.Text.Json.JsonSerializerOptions jsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    private readonly GitHubCopilotConfig config;
    private readonly HttpClient http;
    private readonly HttpClient? ownedHttpClient;

    /// <summary>
    /// Latched <see cref="GitHubCopilotConfig.CopilotTokenUrl"/> once the exchange endpoint has
    /// been found absent (404/50x, a transport failure, or a probe timeout). While the latched
    /// URL matches the current config, <see cref="NeedsRefresh"/> stops triggering the raw-token
    /// self-heal so the endpoint is probed at most once per process; it self-heals on restart,
    /// and a config change to a different URL is not latched and is probed again.
    /// </summary>
    private volatile string? latchedAbsentExchangeUrl;

    public GitHubCopilotProvider(GitHubCopilotConfig? config = null, HttpClient? httpClient = null)
    {
        this.config = config ?? GitHubCopilotConfig.FromEnvironment();
        if (httpClient is null)
        {
            this.ownedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            this.http = this.ownedHttpClient;
        }
        else
        {
            this.http = httpClient;
        }
    }

    public string ProviderId => Id;

    /// <summary>Copilot uses the device flow; the redirect-style login is unsupported.</summary>
    public ILoginFlow BeginLogin(LoginOptions options)
    {
        throw new NotSupportedException(
            "GitHub Copilot uses the device-code flow. Call CredentialManager.LoginWithDeviceCodeAsync(...).");
    }

    public async Task<Credential> LoginWithDeviceCodeAsync(
        LoginOptions options,
        Func<DeviceCodePrompt, CancellationToken, Task> onPrompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onPrompt);

        var device = await this.RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);

        var prompt = new DeviceCodePrompt
        {
            UserCode = device.UserCode!,
            VerificationUri = new Uri(device.VerificationUri!),
            VerificationUriComplete = string.IsNullOrEmpty(device.VerificationUriComplete)
                ? null
                : new Uri(device.VerificationUriComplete),
            ExpiresIn = TimeSpan.FromSeconds(device.ExpiresIn),
            Interval = TimeSpan.FromSeconds(Math.Max(device.Interval, 1)),
        };
        await onPrompt(prompt, cancellationToken).ConfigureAwait(false);

        var gitHubToken = await this.PollForGitHubTokenAsync(device, cancellationToken).ConfigureAwait(false);
        if (!this.config.UseExchange)
        {
            return BuildDirectCredential(gitHubToken);
        }

        // Exchange the device-flow OAuth token for a short-lived Copilot token.
        // If the exchange endpoint is absent on this host (HTTP 404), fall back to the raw
        // token so login succeeds with reduced model entitlement rather than failing outright.
        // Any other failure (4xx/5xx) surfaces as an error.
        var exchanged = await this.ExchangeForCredentialAsync(gitHubToken, cancellationToken).ConfigureAwait(false);
        return exchanged ?? BuildDirectCredential(gitHubToken);
    }

    public bool NeedsRefresh(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.Kind != CredentialKind.OAuth)
        {
            return false;
        }

        // Self-heal for credentials stored before the Enterprise exchange fix: if the config
        // expects an exchanged Copilot token (UseExchange=true) but the stored AccessToken is
        // a raw GitHub OAuth token, trigger a refresh so ExchangeForCredentialAsync is called.
        // A positive test against documented raw-token prefixes is used rather than a negative
        // test for the Copilot token shape — an unrecognised-but-valid token must never be
        // spuriously invalidated.  The exchanged token carries full model entitlement; the raw
        // token yields only a legacy subset.
        //
        // Skip this once the exchange endpoint has been latched absent for the CURRENT
        // CopilotTokenUrl: without this, a raw token never stops being "raw", so every single
        // credential read would re-enter the full refresh path (semaphore + live HTTP probe +
        // an AES-GCM re-encrypt/rewrite of the credential file) forever.
        if (this.config.UseExchange
            && IsRawGitHubToken(credential.AccessToken)
            && !string.Equals(this.latchedAbsentExchangeUrl, this.config.CopilotTokenUrl, StringComparison.Ordinal))
        {
            return true;
        }

        return credential.ExpiresAt.HasValue
            && DateTimeOffset.UtcNow + refreshBuffer >= credential.ExpiresAt.Value;
    }

    public async Task<Credential> RefreshAsync(Credential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrEmpty(credential.RefreshToken))
        {
            throw new TokenRefreshException("No GitHub token available to refresh the Copilot token.");
        }

        // When the exchange endpoint is absent, ExchangeForCredentialAsync returns null and we
        // fall back to a direct credential.  NeedsRefresh's latch (see above) means this only
        // happens once per process for a given CopilotTokenUrl — not on every single call.
        return this.config.UseExchange
            ? await this.ExchangeForCredentialAsync(credential.RefreshToken, cancellationToken).ConfigureAwait(false)
                ?? BuildDirectCredential(credential.RefreshToken)
            : BuildDirectCredential(credential.RefreshToken);
    }

    public AuthHeaders GetAuthHeaders(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.Kind != CredentialKind.OAuth || string.IsNullOrEmpty(credential.AccessToken))
        {
            throw new CredentialNotFoundException("No Copilot token available; log in first.");
        }

        return new AuthHeaders(new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {credential.AccessToken}",
            ["Editor-Version"] = this.config.EditorVersion,
            ["Editor-Plugin-Version"] = this.config.EditorPluginVersion,
            ["Copilot-Integration-Id"] = this.config.IntegrationId,
            ["User-Agent"] = this.config.UserAgent,
            // The Copilot chat endpoint commonly requires X-Initiator; "user" marks a
            // direct user turn (vs "agent" for tool follow-ups). Without it some
            // accounts return 400.
            ["X-Initiator"] = "user",
            // Required by GHE data-residency tenants; accepted (no-op) by the public API.
            ["X-GitHub-Api-Version"] = "2026-06-01",
        });
    }

    private async Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, this.config.DeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = this.config.ClientId,
                ["scope"] = this.config.Scope,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(this.config.UserAgent);

        using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthExchangeException((int)response.StatusCode, body);
        }

        var parsed = System.Text.Json.JsonSerializer.Deserialize<DeviceCodeResponse>(body, jsonOptions);
        if (parsed?.DeviceCode is null || parsed.UserCode is null || parsed.VerificationUri is null)
        {
            throw new LlmAuthException("Device-code response was missing required fields.");
        }

        return parsed;
    }

    private async Task<string> PollForGitHubTokenAsync(DeviceCodeResponse device, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(device.Interval, 1));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new LoginCanceledException("Device-code login expired before the user authorized.");
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, this.config.TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = this.config.ClientId,
                    ["device_code"] = device.DeviceCode!,
                    ["grant_type"] = DeviceGrantType,
                }),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd(this.config.UserAgent);

            using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var token = System.Text.Json.JsonSerializer.Deserialize<DeviceTokenResponse>(body, jsonOptions);

            if (!string.IsNullOrEmpty(token?.AccessToken))
            {
                return token!.AccessToken!;
            }

            switch (token?.Error)
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    // RFC 8628 §3.5: back off using the server-supplied interval when present.
                    interval = TimeSpan.FromSeconds(Math.Max(token!.Interval ?? ((int)interval.TotalSeconds + 5), 1));
                    break;
                case "expired_token":
                    throw new LoginCanceledException("Device code expired; restart the login.");
                case "access_denied":
                    throw new LoginCanceledException("Authorization was denied by the user.");
                case null when token is null || (int)response.StatusCode >= 500:
                    // Transient (unparseable body or 5xx): keep polling until the deadline.
                    break;
                default:
                    // A concrete, unknown error (typically a 4xx config problem) — fail fast.
                    throw new OAuthExchangeException((int)response.StatusCode, body);
            }
        }
    }

    /// <summary>
    /// Returns <see langword="null"/> when the exchange endpoint is classified as absent:
    /// HTTP 404/501/502/503/504, a transport failure (DNS, connection refused, TLS), or our own
    /// bounded probe timeout (see <see cref="exchangeProbeTimeout"/>) firing.  A genuine
    /// caller-initiated cancellation is never swallowed and always propagates.  Throws
    /// <see cref="TokenRefreshException"/> on any other non-success status — in particular
    /// 401/403 (a genuine credentials problem) must never be silently downgraded — and
    /// <see cref="LlmAuthException"/> up front if <see cref="GitHubCopilotConfig.CopilotTokenUrl"/>
    /// is not an absolute https URL without embedded credentials.
    /// </summary>
    private async Task<Credential?> ExchangeForCredentialAsync(string gitHubToken, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(this.config.CopilotTokenUrl, UriKind.Absolute, out var tokenUri)
            || tokenUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(tokenUri.UserInfo))
        {
            // The durable OAuth token is sent as the Authorization header to this URL. A
            // misconfigured value (http://, or a URL carrying embedded userinfo) could put that
            // token on the wire in cleartext or send it to an unintended host. Fail loudly here,
            // at first use, rather than risk silently contacting the wrong endpoint.
            throw new LlmAuthException(
                "Copilot token exchange URL must be an absolute https URL without embedded credentials.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, this.config.CopilotTokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", gitHubToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(this.config.UserAgent);
        request.Headers.TryAddWithoutValidation("Editor-Version", this.config.EditorVersion);

        // Bound the probe independently of the HttpClient's own (30s) timeout: this is a
        // best-effort probe against a host that may not even have the endpoint, and it must not
        // consume the caller's whole request budget.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(exchangeProbeTimeout);

        HttpResponseMessage response;
        try
        {
            response = await this.http.SendAsync(request, probeCts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // DNS failure / connection refused / TLS mismatch on a host that was never
            // contacted before this exchange existed: treat exactly like an absent endpoint
            // rather than breaking every login/refresh on this host.
            this.latchedAbsentExchangeUrl = this.config.CopilotTokenUrl;
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own probe timeout fired (the caller's token is NOT canceled): treat like an
            // absent endpoint. A genuine caller-initiated cancellation does not match this
            // filter and propagates unchanged.
            this.latchedAbsentExchangeUrl = this.config.CopilotTokenUrl;
            return null;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (IsExchangeAbsentStatus(response.StatusCode))
            {
                // 404: the endpoint itself is absent on this host. 501/502/503/504: commonly a
                // proxy or wildcard host with nothing behind it for this path. Both are treated
                // the same — fall back to a direct credential rather than failing outright.
                this.latchedAbsentExchangeUrl = this.config.CopilotTokenUrl;
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Do not surface the raw body (it can carry token material) in the message.
                throw new TokenRefreshException(
                    $"Copilot token exchange failed (HTTP {(int)response.StatusCode}).");
            }

            var copilot = System.Text.Json.JsonSerializer.Deserialize<CopilotTokenResponse>(body, jsonOptions);
            if (string.IsNullOrEmpty(copilot?.Token))
            {
                throw new TokenRefreshException("Copilot token exchange returned no token.");
            }

            return new Credential
            {
                ProviderId = Id,
                Kind = CredentialKind.OAuth,
                AccessToken = copilot!.Token,
                RefreshToken = gitHubToken,
                ExpiresAt = copilot.ExpiresAt > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(copilot.ExpiresAt)
                    : null,
            };
        }
    }

    private static bool IsExchangeAbsentStatus(HttpStatusCode status) =>
        status is HttpStatusCode.NotFound
            or HttpStatusCode.NotImplemented
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// Build a credential where the raw GitHub device-flow OAuth token IS the bearer
    /// (no exchange). Used for GHE data-residency tenants where <c>copilot-api.{host}</c>
    /// accepts the token directly. <see cref="Credential.ExpiresAt"/> is left null so
    /// <see cref="NeedsRefresh"/> never triggers an unnecessary re-poll, and
    /// <see cref="Credential.RefreshToken"/> is set to the same token so
    /// <see cref="RefreshAsync"/> can still be driven explicitly if needed.
    /// </summary>
    private static Credential BuildDirectCredential(string gitHubToken) =>
        new()
        {
            ProviderId = Id,
            Kind = CredentialKind.OAuth,
            AccessToken = gitHubToken,
            RefreshToken = gitHubToken,
            ExpiresAt = null,
        };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="token"/> is a raw GitHub
    /// OAuth token identified by a documented GitHub token prefix.  A positive prefix
    /// test is used rather than a negative test for the Copilot token shape so that
    /// any unrecognised-but-valid token is never spuriously flagged for refresh.
    /// </summary>
    private static bool IsRawGitHubToken(string? token) =>
        token is not null
        && (token.StartsWith("ghu_", StringComparison.Ordinal)
            || token.StartsWith("gho_", StringComparison.Ordinal)
            || token.StartsWith("ghp_", StringComparison.Ordinal)
            || token.StartsWith("ghs_", StringComparison.Ordinal)
            || token.StartsWith("ghr_", StringComparison.Ordinal)
            || token.StartsWith("ghe_", StringComparison.Ordinal)
            || token.StartsWith("github_pat_", StringComparison.Ordinal));

    public void Dispose()
    {
        this.ownedHttpClient?.Dispose();
    }
}
