namespace Coda.Tui;

/// <summary>
/// Bridges the persisted <c>githubEnterpriseDomain</c> setting to the environment variable
/// (<c>GH_COPILOT_ENTERPRISE_DOMAIN</c>) that <see cref="LlmAuth.Providers.GitHubCopilot.GitHubCopilotConfig.FromEnvironment"/>
/// reads. Hydrating the process environment once at startup means every consumer — the auth
/// provider AND the chat-client factory — resolves the same GitHub Copilot host, so an
/// enterprise user configures it once and never has to re-enter it or set an env var by hand.
/// </summary>
internal static class CopilotEnvironment
{
    private const string EnterpriseDomainVar = "GH_COPILOT_ENTERPRISE_DOMAIN";

    /// <summary>
    /// Set <c>GH_COPILOT_ENTERPRISE_DOMAIN</c> for this process from the persisted setting,
    /// unless it is already set (an explicit env var always wins). Blank/null is a no-op.
    /// </summary>
    public static void ApplyEnterpriseDomain(string? domain)
    {
        if (!string.IsNullOrWhiteSpace(domain)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnterpriseDomainVar)))
        {
            Environment.SetEnvironmentVariable(EnterpriseDomainVar, domain.Trim());
        }
    }
}
