using Coda.Agent.Settings;
using Coda.Sdk.Providers;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;

namespace Engine.Tests.Providers;

/// <summary>
/// The single source of truth for resolving a runner's effective (provider, model)
/// from an explicit flag then the persisted settings default — with NO built-in
/// fallback. When neither configures a value it resolves to null and
/// <see cref="ProviderModelResolver.Require"/> fails fast, instead of silently
/// defaulting to the Anthropic/Claude.ai provider as the old code did.
/// </summary>
public sealed class ProviderModelResolverTests
{
    [Fact]
    public void Resolves_provider_and_model_from_settings_when_no_flags()
    {
        var settings = CodaSettings.Empty with { DefaultProvider = "github-copilot", DefaultModel = "claude-opus-4-8" };

        var (providerId, model) = ProviderModelResolver.Resolve(providerFlag: null, modelFlag: null, settings);

        Assert.Equal(GitHubCopilotProvider.Id, providerId);
        Assert.Equal("claude-opus-4-8", model);
    }

    [Fact]
    public void No_flag_and_no_settings_resolves_to_null_without_inventing_a_default()
    {
        var (providerId, model) = ProviderModelResolver.Resolve(providerFlag: null, modelFlag: null, CodaSettings.Empty);

        Assert.Null(providerId);
        Assert.Null(model);
    }

    [Fact]
    public void Blank_settings_values_resolve_to_null()
    {
        var settings = CodaSettings.Empty with { DefaultProvider = "   ", DefaultModel = "" };

        var (providerId, model) = ProviderModelResolver.Resolve(providerFlag: null, modelFlag: null, settings);

        Assert.Null(providerId);
        Assert.Null(model);
    }

    [Fact]
    public void Explicit_flags_win_over_settings()
    {
        var settings = CodaSettings.Empty with { DefaultProvider = "github-copilot", DefaultModel = "settings-model" };

        var (providerId, model) = ProviderModelResolver.Resolve(providerFlag: "claude", modelFlag: "flag-model", settings);

        Assert.Equal(ClaudeAiProvider.Id, providerId);
        Assert.Equal("flag-model", model);
    }

    [Fact]
    public void Require_throws_when_provider_missing()
    {
        var ex = Assert.Throws<ProviderModelNotConfiguredException>(
            () => ProviderModelResolver.Require(providerId: null, model: "some-model"));

        Assert.Contains("provider", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Require_throws_when_model_missing()
    {
        var ex = Assert.Throws<ProviderModelNotConfiguredException>(
            () => ProviderModelResolver.Require(providerId: GitHubCopilotProvider.Id, model: null));

        Assert.Contains("model", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Require_returns_the_pair_when_both_configured()
    {
        var (providerId, model) = ProviderModelResolver.Require(GitHubCopilotProvider.Id, "claude-opus-4-8");

        Assert.Equal(GitHubCopilotProvider.Id, providerId);
        Assert.Equal("claude-opus-4-8", model);
    }
}
