using Coda.Agent.Settings;
using Coda.Sdk.Providers;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;

namespace Engine.Tests.Providers;

/// <summary>
/// The single source of truth for resolving a runner's effective (provider, model).
/// Provider resolves from an explicit flag → the connected credential; there is NO built-in
/// provider fallback, and <c>settings.DefaultProvider</c> is not a selector. The MODEL belongs to
/// the resolved provider: flag → the provider's configured model (<c>modelByProvider</c>) → the
/// provider's built-in default. There is intentionally NO provider-agnostic default model, so a
/// model configured for one provider can never leak to another.
/// </summary>
public sealed class ProviderModelResolverTests
{
    [Fact]
    public void No_flag_and_no_connected_resolves_to_null()
    {
        var (providerId, model) = ProviderModelResolver.Resolve(providerFlag: null, modelFlag: null, CodaSettings.Empty, connectedProviderId: null);

        Assert.Null(providerId);
        Assert.Null(model);
    }

    [Fact]
    public void Blank_flags_resolve_to_null()
    {
        var (providerId, model) = ProviderModelResolver.Resolve(providerFlag: "   ", modelFlag: "", CodaSettings.Empty, connectedProviderId: null);

        Assert.Null(providerId);
        Assert.Null(model);
    }

    [Fact]
    public void Settings_DefaultProvider_is_not_a_selector()
    {
        var settings = CodaSettings.Empty with { DefaultProvider = "github-copilot" };

        var (providerId, _) = ProviderModelResolver.Resolve(providerFlag: null, modelFlag: null, settings, connectedProviderId: null);

        Assert.Null(providerId); // only a flag or a connected credential selects a provider
    }

    [Fact]
    public void Explicit_flags_win()
    {
        var (providerId, model) = ProviderModelResolver.Resolve(providerFlag: "claude", modelFlag: "flag-model", CodaSettings.Empty, connectedProviderId: null);

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
        var (providerId, model) = ProviderModelResolver.Require(GitHubCopilotProvider.Id, "claude-opus-4.8");

        Assert.Equal(GitHubCopilotProvider.Id, providerId);
        Assert.Equal("claude-opus-4.8", model);
    }

    // ── provider: flag → connected credential ──────────────────────────────────

    [Fact]
    public void No_flag_uses_connected_provider()
    {
        var (provider, _) = ProviderModelResolver.Resolve(null, null, CodaSettings.Empty, connectedProviderId: GitHubCopilotProvider.Id);

        Assert.Equal(GitHubCopilotProvider.Id, provider);
    }

    [Fact]
    public void Flag_overrides_connected_provider()
    {
        var (provider, _) = ProviderModelResolver.Resolve("claude", null, CodaSettings.Empty, connectedProviderId: GitHubCopilotProvider.Id);

        Assert.Equal(ClaudeAiProvider.Id, provider);
    }

    // ── model belongs to the provider (modelByProvider + built-in; NO global default) ──

    [Fact]
    public void ModelByProvider_used_for_the_resolved_provider()
    {
        var settings = CodaSettings.Empty with
        {
            ModelByProvider = new Dictionary<string, string> { [GitHubCopilotProvider.Id] = "configured-model" },
        };

        var (provider, model) = ProviderModelResolver.Resolve(null, null, settings, connectedProviderId: GitHubCopilotProvider.Id);

        Assert.Equal(GitHubCopilotProvider.Id, provider);
        Assert.Equal("configured-model", model);
    }

    [Fact]
    public void ModelByProvider_does_not_leak_to_another_provider()
    {
        // A model set for Copilot must NOT be used when the connected provider is Claude.ai —
        // it falls back to Claude's own built-in default instead.
        var settings = CodaSettings.Empty with
        {
            ModelByProvider = new Dictionary<string, string> { [GitHubCopilotProvider.Id] = "copilot-only-model" },
        };

        var (provider, model) = ProviderModelResolver.Resolve(null, null, settings, connectedProviderId: ClaudeAiProvider.Id);

        Assert.Equal(ClaudeAiProvider.Id, provider);
        Assert.NotEqual("copilot-only-model", model);
        Assert.Equal(ProviderDefaults.ModelFor(ClaudeAiProvider.Id), model);
    }

    [Fact]
    public void ResolvedProvider_with_no_configured_model_falls_back_to_provider_built_in()
    {
        var (provider, model) = ProviderModelResolver.Resolve(null, null, CodaSettings.Empty, connectedProviderId: GitHubCopilotProvider.Id);

        Assert.Equal(GitHubCopilotProvider.Id, provider);
        Assert.False(string.IsNullOrWhiteSpace(model));
        Assert.Equal(ProviderDefaults.ModelFor(GitHubCopilotProvider.Id), model);
    }

    [Fact]
    public void Model_flag_wins_over_modelByProvider()
    {
        var settings = CodaSettings.Empty with
        {
            ModelByProvider = new Dictionary<string, string> { [GitHubCopilotProvider.Id] = "configured-model" },
        };

        var (_, model) = ProviderModelResolver.Resolve(null, modelFlag: "flag-model", settings, connectedProviderId: GitHubCopilotProvider.Id);

        Assert.Equal("flag-model", model);
    }

    [Fact]
    public void ModelByProvider_used_when_provider_comes_from_flag()
    {
        // Mirrors the interactive TUI startup call: Resolve(startupProvider.Id, CODA_MODEL, settings, connected).
        var settings = CodaSettings.Empty with
        {
            ModelByProvider = new Dictionary<string, string> { [GitHubCopilotProvider.Id] = "configured-model" },
        };

        var (provider, model) = ProviderModelResolver.Resolve(
            providerFlag: GitHubCopilotProvider.Id, modelFlag: null, settings, connectedProviderId: GitHubCopilotProvider.Id);

        Assert.Equal(GitHubCopilotProvider.Id, provider);
        Assert.Equal("configured-model", model);
    }
}
