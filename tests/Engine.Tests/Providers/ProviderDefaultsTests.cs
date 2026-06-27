using Coda.Sdk.Providers;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests.Providers;

public sealed class ProviderDefaultsTests
{
    public static TheoryData<string, string> Cases() => new()
    {
        // GitHub Copilot → Copilot default.
        { GitHubCopilotProvider.Id, CopilotModels.DefaultModel },
        // Claude.ai → Anthropic default.
        { ClaudeAiProvider.Id, AnthropicModels.DefaultModel },
        // Anthropic API key → Anthropic default.
        { ApiKeyProvider.Id, AnthropicModels.DefaultModel },
        // Any other / unknown provider id → Anthropic default (the else branch).
        { "custom-thing", AnthropicModels.DefaultModel },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ModelFor_returns_provider_default(string providerId, string expected)
    {
        Assert.Equal(expected, ProviderDefaults.ModelFor(providerId));
    }
}
