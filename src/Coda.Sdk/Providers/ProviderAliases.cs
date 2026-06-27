using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;

namespace Coda.Sdk.Providers;

/// <summary>
/// Single source of truth for resolving a user-supplied provider token
/// (from <c>--provider</c>, <c>CODA_PROVIDER</c>, or persisted settings) to a
/// canonical provider id. Trims and lowercases the token, maps well-known
/// aliases to the real <c>*Provider.Id</c> constants, and passes any unknown
/// token through verbatim (trimmed). Previously copy-pasted across the TUI and
/// the serve/run/models runners.
/// </summary>
public static class ProviderAliases
{
    /// <summary>
    /// Resolve a provider token to a canonical provider id.
    /// </summary>
    /// <param name="token">
    /// The raw provider token. <see langword="null"/>, empty, or whitespace-only
    /// resolves to <see cref="ClaudeAiProvider.Id"/>. Recognized aliases map to
    /// their canonical id; any other value is returned trimmed but otherwise
    /// unchanged (preserving its original casing).
    /// </param>
    /// <returns>The canonical provider id.</returns>
    public static string Resolve(string? token)
    {
        var trimmed = token?.Trim();
        return trimmed?.ToLowerInvariant() switch
        {
            null or "" or "claude" or "claude-ai" => ClaudeAiProvider.Id,
            "copilot" or "github" or "github-copilot" => GitHubCopilotProvider.Id,
            "apikey" or "api-key" or "anthropic-api-key" => ApiKeyProvider.Id,
            _ => trimmed!,
        };
    }
}
