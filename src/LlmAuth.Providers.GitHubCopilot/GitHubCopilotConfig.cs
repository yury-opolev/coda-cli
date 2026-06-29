namespace LlmAuth.Providers.GitHubCopilot;

/// <summary>
/// GitHub Copilot endpoints, OAuth client id, and the editor headers the Copilot
/// API expects. These reflect public/community knowledge of the GitHub Device
/// Flow + Copilot token exchange used by editor integrations, and every value is
/// overridable (constructor or <see cref="FromEnvironment"/>).
/// Using the Copilot API outside official editors is subject to GitHub's ToS.
/// </summary>
public sealed record GitHubCopilotConfig
{
    /// <summary>OAuth app client id used by the VS Code Copilot extension's device flow.</summary>
    public required string ClientId { get; init; }

    /// <summary>Device-code request endpoint (RFC 8628).</summary>
    public required string DeviceCodeUrl { get; init; }

    /// <summary>Device-grant token polling endpoint.</summary>
    public required string TokenUrl { get; init; }

    /// <summary>Exchanges the GitHub OAuth token for a short-lived Copilot token.</summary>
    public required string CopilotTokenUrl { get; init; }

    /// <summary>OAuth scope requested in the device flow.</summary>
    public required string Scope { get; init; }

    /// <summary>Copilot chat/completions API base.</summary>
    public required string ApiBaseUrl { get; init; }

    /// <summary>Editor-Version header value.</summary>
    public required string EditorVersion { get; init; }

    /// <summary>Editor-Plugin-Version header value.</summary>
    public required string EditorPluginVersion { get; init; }

    /// <summary>Copilot-Integration-Id header value.</summary>
    public required string IntegrationId { get; init; }

    /// <summary>User-Agent for Copilot requests.</summary>
    public required string UserAgent { get; init; }

    /// <summary>
    /// When <see langword="true"/> (the default), the raw GitHub OAuth token is
    /// exchanged for a short-lived Copilot token via <see cref="CopilotTokenUrl"/>.
    /// Set to <see langword="false"/> for GitHub Enterprise data-residency tenants,
    /// where the raw device-flow OAuth token is the bearer directly and no exchange
    /// endpoint is available.
    /// </summary>
    public bool UseExchange { get; init; } = true;

    /// <summary>Default configuration (VS Code Copilot-style values, public github.com).</summary>
    public static GitHubCopilotConfig Default { get; } = new()
    {
        ClientId = "Iv1.b507a08c87ecfe98",
        DeviceCodeUrl = "https://github.com/login/device/code",
        TokenUrl = "https://github.com/login/oauth/access_token",
        CopilotTokenUrl = "https://api.github.com/copilot_internal/v2/token",
        Scope = "read:user",
        ApiBaseUrl = "https://api.githubcopilot.com",
        EditorVersion = "vscode/1.95.0",
        EditorPluginVersion = "copilot-chat/0.22.0",
        IntegrationId = "vscode-chat",
        UserAgent = "GitHubCopilotChat/0.22.0",
    };

    /// <summary>
    /// Build a configuration for a GitHub Enterprise data-residency tenant.
    /// Device-code and token endpoints are on the GHE host; the inference endpoint
    /// is <c>https://copilot-api.{domain}</c>; the dotcom token exchange is skipped
    /// (<see cref="UseExchange"/> is <see langword="false"/>).
    /// ClientId and editor headers are inherited from <see cref="Default"/>.
    /// </summary>
    /// <param name="domain">
    /// The GHE host, e.g. <c>microsoft.ghe.com</c>. Leading <c>https://</c> /
    /// <c>http://</c> and trailing slashes are stripped automatically.
    /// </param>
    public static GitHubCopilotConfig ForEnterprise(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var d = domain
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        // Recover the GHE host if the caller pasted the Copilot host (copilot-api.<ghe>) by
        // mistake, so the device/token AND inference URLs are all derived consistently and we
        // never produce a doubled "copilot-api." prefix.
        if (d.StartsWith("copilot-api.", StringComparison.OrdinalIgnoreCase))
        {
            d = d["copilot-api.".Length..];
        }

        return Default with
        {
            DeviceCodeUrl = $"https://{d}/login/device/code",
            TokenUrl = $"https://{d}/login/oauth/access_token",
            ApiBaseUrl = $"https://copilot-api.{d}",
            UseExchange = false,
        };
    }

    /// <summary>
    /// Apply environment overrides for the volatile values.
    /// <para>
    /// If <c>GH_COPILOT_ENTERPRISE_DOMAIN</c> is set, the base config starts from
    /// <see cref="ForEnterprise"/> instead of <see cref="Default"/>. Individual URL
    /// and flag overrides are then applied on top.
    /// </para>
    /// <para>
    /// Recognised environment variables:
    /// <c>GH_COPILOT_ENTERPRISE_DOMAIN</c>,
    /// <c>GH_COPILOT_CLIENT_ID</c>,
    /// <c>GH_COPILOT_DEVICE_CODE_URL</c>,
    /// <c>GH_COPILOT_TOKEN_URL</c>,
    /// <c>GH_COPILOT_COPILOT_TOKEN_URL</c>,
    /// <c>GH_COPILOT_API_BASE_URL</c>,
    /// <c>GH_COPILOT_USE_EXCHANGE</c>,
    /// <c>GH_COPILOT_EDITOR_VERSION</c>,
    /// <c>GH_COPILOT_PLUGIN_VERSION</c>,
    /// <c>GH_COPILOT_INTEGRATION_ID</c>,
    /// <c>GH_COPILOT_USER_AGENT</c>.
    /// </para>
    /// </summary>
    public static GitHubCopilotConfig FromEnvironment()
    {
        var enterpriseDomain = Env("GH_COPILOT_ENTERPRISE_DOMAIN");
        var config = enterpriseDomain is not null
            ? ForEnterprise(enterpriseDomain)
            : Default;

        var useExchangeRaw = Env("GH_COPILOT_USE_EXCHANGE");
        var useExchange = useExchangeRaw is not null
            ? !string.Equals(useExchangeRaw, "false", StringComparison.OrdinalIgnoreCase)
              && useExchangeRaw != "0"
            : config.UseExchange;

        return config with
        {
            ClientId = Env("GH_COPILOT_CLIENT_ID") ?? config.ClientId,
            DeviceCodeUrl = Env("GH_COPILOT_DEVICE_CODE_URL") ?? config.DeviceCodeUrl,
            TokenUrl = Env("GH_COPILOT_TOKEN_URL") ?? config.TokenUrl,
            CopilotTokenUrl = Env("GH_COPILOT_COPILOT_TOKEN_URL") ?? config.CopilotTokenUrl,
            ApiBaseUrl = Env("GH_COPILOT_API_BASE_URL") ?? config.ApiBaseUrl,
            UseExchange = useExchange,
            EditorVersion = Env("GH_COPILOT_EDITOR_VERSION") ?? config.EditorVersion,
            EditorPluginVersion = Env("GH_COPILOT_PLUGIN_VERSION") ?? config.EditorPluginVersion,
            IntegrationId = Env("GH_COPILOT_INTEGRATION_ID") ?? config.IntegrationId,
            UserAgent = Env("GH_COPILOT_USER_AGENT") ?? config.UserAgent,
        };

        static string? Env(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
