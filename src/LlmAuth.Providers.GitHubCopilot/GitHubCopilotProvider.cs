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
    private static readonly System.Text.Json.JsonSerializerOptions jsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    private readonly GitHubCopilotConfig config;
    private readonly HttpClient http;
    private readonly HttpClient? ownedHttpClient;

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
        return this.config.UseExchange
            ? await this.ExchangeForCredentialAsync(gitHubToken, cancellationToken).ConfigureAwait(false)
            : BuildDirectCredential(gitHubToken);
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
            throw new TokenRefreshException("No GitHub token available to refresh the Copilot token.");
        }

        return this.config.UseExchange
            ? await this.ExchangeForCredentialAsync(credential.RefreshToken, cancellationToken).ConfigureAwait(false)
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

    private async Task<Credential> ExchangeForCredentialAsync(string gitHubToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, this.config.CopilotTokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", gitHubToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(this.config.UserAgent);
        request.Headers.TryAddWithoutValidation("Editor-Version", this.config.EditorVersion);

        using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

    public void Dispose()
    {
        this.ownedHttpClient?.Dispose();
    }
}
