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

    /// <summary>Default configuration (VS Code Copilot-style values).</summary>
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
    /// Apply environment overrides for the volatile values
    /// (<c>GH_COPILOT_CLIENT_ID</c>, <c>GH_COPILOT_EDITOR_VERSION</c>,
    /// <c>GH_COPILOT_PLUGIN_VERSION</c>, <c>GH_COPILOT_INTEGRATION_ID</c>,
    /// <c>GH_COPILOT_USER_AGENT</c>).
    /// </summary>
    public static GitHubCopilotConfig FromEnvironment()
    {
        var config = Default;
        return config with
        {
            ClientId = Env("GH_COPILOT_CLIENT_ID") ?? config.ClientId,
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
