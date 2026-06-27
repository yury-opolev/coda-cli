namespace LlmAuth.Providers.ClaudeAi;

/// <summary>
/// Claude.ai / Anthropic OAuth endpoints and scopes, matching the Claude Code
/// v2.1.156 client. Defaults to production; <see cref="FromEnvironment"/> honors the same
/// env overrides the CLI does so the client behaves identically.
/// </summary>
public sealed record ClaudeAiOAuthConfig
{
    public const string InferenceScope = "user:inference";
    public const string ProfileScope = "user:profile";
    public const string OAuthBetaHeader = "oauth-2025-04-20";

    /// <summary>CONSOLE_OAUTH_SCOPES.</summary>
    public static readonly string[] ConsoleScopes = ["org:create_api_key", "user:profile"];

    /// <summary>CLAUDE_AI_OAUTH_SCOPES (used for refresh).</summary>
    public static readonly string[] ClaudeAiScopes =
    [
        "user:profile",
        "user:inference",
        "user:sessions:claude_code",
        "user:mcp_servers",
        "user:file_upload",
    ];

    // Allowed bases for CLAUDE_CODE_CUSTOM_OAUTH_URL (FedStart/PubSec only),
    // matching ALLOWED_OAUTH_BASE_URLS in the source.
    private static readonly string[] AllowedCustomBases =
    [
        "https://beacon.claude-ai.staging.ant.dev",
        "https://claude.fedstart.com",
        "https://claude-staging.fedstart.com",
    ];

    public required string ClientId { get; init; }
    public required string BaseApiUrl { get; init; }
    public required string ConsoleAuthorizeUrl { get; init; }
    public required string ClaudeAiAuthorizeUrl { get; init; }
    public required string TokenUrl { get; init; }
    public required string ApiKeyUrl { get; init; }
    public required string RolesUrl { get; init; }
    public required string ManualRedirectUrl { get; init; }

    /// <summary>
    /// ALL_OAUTH_SCOPES: the dedup-union of console + claude.ai scopes, first-seen
    /// order preserved — requested at authorize time to cover both flows.
    /// </summary>
    public IReadOnlyList<string> AllScopes { get; init; } = BuildAllScopes();

    /// <summary>
    /// Production OAuth endpoints. No OAuth client id is bundled: supply your own via the
    /// <c>CLAUDE_CODE_OAUTH_CLIENT_ID</c> environment variable to use subscription OAuth login.
    /// Otherwise authenticate with an Anthropic API key (<c>ApiKeyProvider</c>) or GitHub Copilot.
    /// </summary>
    public static ClaudeAiOAuthConfig Prod { get; } = new()
    {
        ClientId = string.Empty,
        BaseApiUrl = "https://api.anthropic.com",
        ConsoleAuthorizeUrl = "https://platform.claude.com/oauth/authorize",
        ClaudeAiAuthorizeUrl = "https://claude.com/cai/oauth/authorize",
        TokenUrl = "https://platform.claude.com/v1/oauth/token",
        ApiKeyUrl = "https://api.anthropic.com/api/oauth/claude_cli/create_api_key",
        RolesUrl = "https://api.anthropic.com/api/oauth/claude_cli/roles",
        ManualRedirectUrl = "https://platform.claude.com/oauth/code/callback",
    };

    /// <summary>Staging OAuth endpoints. No client id is bundled (see <see cref="Prod"/>).</summary>
    public static ClaudeAiOAuthConfig Staging { get; } = new()
    {
        ClientId = string.Empty,
        BaseApiUrl = "https://api-staging.anthropic.com",
        ConsoleAuthorizeUrl = "https://platform.staging.ant.dev/oauth/authorize",
        ClaudeAiAuthorizeUrl = "https://claude-ai.staging.ant.dev/oauth/authorize",
        TokenUrl = "https://platform.staging.ant.dev/v1/oauth/token",
        ApiKeyUrl = "https://api-staging.anthropic.com/api/oauth/claude_cli/create_api_key",
        RolesUrl = "https://api-staging.anthropic.com/api/oauth/claude_cli/roles",
        ManualRedirectUrl = "https://platform.staging.ant.dev/oauth/code/callback",
    };

    /// <summary>
    /// Resolve config the way <c>getOauthConfig()</c> does: prod by default;
    /// staging when <c>USER_TYPE=ant</c> + <c>USE_STAGING_OAUTH</c>; then apply
    /// <c>CLAUDE_CODE_CUSTOM_OAUTH_URL</c> (allowlisted) and
    /// <c>CLAUDE_CODE_OAUTH_CLIENT_ID</c> overrides.
    /// </summary>
    public static ClaudeAiOAuthConfig FromEnvironment()
    {
        var isAnt = string.Equals(Environment.GetEnvironmentVariable("USER_TYPE"), "ant", StringComparison.Ordinal);
        var useStaging = IsTruthy(Environment.GetEnvironmentVariable("USE_STAGING_OAUTH"));
        var config = isAnt && useStaging ? Staging : Prod;

        var customBase = Environment.GetEnvironmentVariable("CLAUDE_CODE_CUSTOM_OAUTH_URL");
        if (!string.IsNullOrEmpty(customBase))
        {
            var baseUrl = customBase.TrimEnd('/');
            if (!AllowedCustomBases.Contains(baseUrl))
            {
                throw new LlmAuthException("CLAUDE_CODE_CUSTOM_OAUTH_URL is not an approved endpoint.");
            }

            config = config with
            {
                BaseApiUrl = baseUrl,
                ConsoleAuthorizeUrl = $"{baseUrl}/oauth/authorize",
                ClaudeAiAuthorizeUrl = $"{baseUrl}/oauth/authorize",
                TokenUrl = $"{baseUrl}/v1/oauth/token",
                ApiKeyUrl = $"{baseUrl}/api/oauth/claude_cli/create_api_key",
                RolesUrl = $"{baseUrl}/api/oauth/claude_cli/roles",
                ManualRedirectUrl = $"{baseUrl}/oauth/code/callback",
            };
        }

        var clientIdOverride = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_CLIENT_ID");
        if (!string.IsNullOrEmpty(clientIdOverride))
        {
            config = config with { ClientId = clientIdOverride };
        }

        return config;
    }

    private static string[] BuildAllScopes()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var scope in ConsoleScopes.Concat(ClaudeAiScopes))
        {
            if (seen.Add(scope))
            {
                ordered.Add(scope);
            }
        }

        return [.. ordered];
    }

    private static bool IsTruthy(string? value)
    {
        return value is not null &&
            (value == "1" ||
             string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase));
    }
}
