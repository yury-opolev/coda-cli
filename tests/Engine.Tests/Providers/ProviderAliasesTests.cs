using Coda.Sdk.Providers;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;

namespace Engine.Tests.Providers;

public sealed class ProviderAliasesTests
{
    public static TheoryData<string?, string> Cases() => new()
    {
        // null / empty → Claude.ai default.
        { null, ClaudeAiProvider.Id },
        { "", ClaudeAiProvider.Id },
        // Claude.ai aliases.
        { "claude", ClaudeAiProvider.Id },
        { "claude-ai", ClaudeAiProvider.Id },
        // GitHub Copilot aliases.
        { "copilot", GitHubCopilotProvider.Id },
        { "github", GitHubCopilotProvider.Id },
        { "github-copilot", GitHubCopilotProvider.Id },
        // Anthropic API-key aliases.
        { "apikey", ApiKeyProvider.Id },
        { "api-key", ApiKeyProvider.Id },
        { "anthropic-api-key", ApiKeyProvider.Id },
        // Case-insensitive.
        { "CLAUDE", ClaudeAiProvider.Id },
        { "GitHub-Copilot", GitHubCopilotProvider.Id },
        // Whitespace tolerance — the trim-drift bug fix.
        { "  claude  ", ClaudeAiProvider.Id },
        { "\tcopilot\n", GitHubCopilotProvider.Id },
        { "  api-key  ", ApiKeyProvider.Id },
        // Unknown token → passed through verbatim (trimmed).
        { "custom-thing", "custom-thing" },
        { "  custom-thing  ", "custom-thing" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Resolve_maps_alias_or_passes_through(string? token, string expected)
    {
        Assert.Equal(expected, ProviderAliases.Resolve(token));
    }

    [Fact]
    public void Whitespace_only_token_resolves_to_claude_default()
    {
        // Trimming a whitespace-only token yields "", which is the Claude.ai default.
        Assert.Equal(ClaudeAiProvider.Id, ProviderAliases.Resolve("   "));
    }
}
