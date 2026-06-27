using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;

namespace Engine.Tests.TestSupport;

/// <summary>
/// Shared <see cref="CredentialManager"/> fixtures over an <see cref="InMemoryTokenStore"/> with the
/// standard provider set (Claude, ApiKey, Copilot), pre-loaded with a stored OAuth access token.
/// </summary>
internal static class CredentialFixtures
{
    /// <summary>A credential manager signed in to the Claude provider with a stored OAuth token.</summary>
    public static CredentialManager SignedInClaude()
    {
        var creds = NewManager();
        creds.StoreAsync(ClaudeAiProvider.Id, new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "AT",
        }).GetAwaiter().GetResult();
        return creds;
    }

    /// <summary>A credential manager signed in to BOTH the Claude and Copilot providers.</summary>
    public static CredentialManager SignedInClaudeAndCopilot()
    {
        var creds = SignedInClaude();
        creds.StoreAsync(GitHubCopilotProvider.Id, new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "AT",
        }).GetAwaiter().GetResult();
        return creds;
    }

    private static CredentialManager NewManager() =>
        new(new InMemoryTokenStore(), [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
}
