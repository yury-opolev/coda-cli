using Coda.Agent.Settings;
using Coda.Sdk.Providers;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;

namespace Engine.Tests.Providers;

/// <summary>
/// The single source of truth for resolving a runner's effective (provider, model).
/// Provider resolves from an explicit flag → connected credential. Model resolves
/// from an explicit flag → the persisted settings default. There is NO built-in
/// fallback: when neither configures a value it resolves to null and
/// <see cref="ProviderModelResolver.Require"/> fails fast, instead of silently
/// defaulting to the Anthropic/Claude.ai provider as the old code did.
/// <c>settings.DefaultProvider</c> is no longer a provider selector. (The
/// transitional 3-arg overload — connected credential always null — was removed
/// once every caller migrated to the 4-arg overload; these cases now exercise it
/// with <c>connectedProviderId: null</c> directly.)
/// </summary>
public sealed class ProviderModelResolverTests
{
    [Fact]
    public void NoConnectedProvider_does_not_select_provider_from_settings_default()
    {
        var settings = CodaSettings.Empty with { DefaultProvider = "github-copilot", DefaultModel = "claude-opus-4-8" };

        var (providerId, model) = ProviderModelResolver.Resolve(providerFlag: null, modelFlag: null, settings, connectedProviderId: null);

        Assert.Null(providerId);
        Assert.Equal("claude-opus-4-8", model);
    }

    [Fact]
    public void No_flag_and_no_settings_resolves_to_null_without_inventing_a_default()
    {
        var (providerId, model) = ProviderModelResolver.Resolve(providerFlag: null, modelFlag: null, CodaSettings.Empty, connectedProviderId: null);

        Assert.Null(providerId);
        Assert.Null(model);
    }

    [Fact]
    public void Blank_settings_values_resolve_to_null()
    {
        var settings = CodaSettings.Empty with { DefaultProvider = "   ", DefaultModel = "" };

        var (providerId, model) = ProviderModelResolver.Resolve(providerFlag: null, modelFlag: null, settings, connectedProviderId: null);

        Assert.Null(providerId);
        Assert.Null(model);
    }

    [Fact]
    public void Explicit_flags_win_over_settings()
    {
        var settings = CodaSettings.Empty with { DefaultProvider = "github-copilot", DefaultModel = "settings-model" };

        var (providerId, model) = ProviderModelResolver.Resolve(providerFlag: "claude", modelFlag: "flag-model", settings, connectedProviderId: null);

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

    [Fact]
    public void Resolve_NoFlag_UsesConnectedProvider_NotSettingsDefault()
    {
        var settings = CodaSettings.Empty with { DefaultProvider = "anthropic-api-key", DefaultModel = "m1" };
        var (provider, model) = ProviderModelResolver.Resolve(
            providerFlag: null, modelFlag: null, settings, connectedProviderId: "github-copilot");
        Assert.Equal(ProviderAliases.Resolve("github-copilot"), provider);
        Assert.Equal("m1", model);
    }

    [Fact]
    public void Resolve_Flag_OverridesConnected()
    {
        var settings = CodaSettings.Empty;
        var (provider, _) = ProviderModelResolver.Resolve("claude", null, settings, connectedProviderId: "github-copilot");
        Assert.Equal(ProviderAliases.Resolve("claude"), provider);
    }

    [Fact]
    public void Resolve_NoFlagNoConnected_ProviderIsNull()
    {
        var (provider, _) = ProviderModelResolver.Resolve(null, null, CodaSettings.Empty, connectedProviderId: null);
        Assert.Null(provider);
    }

    // ── the model belongs to the provider (defaultModelByProvider + built-in fallback) ──

    [Fact]
    public void PerProviderModel_WinsOverGlobalDefault()
    {
        var settings = CodaSettings.Empty with
        {
            DefaultModel = "global-model",
            DefaultModelByProvider = new Dictionary<string, string> { [GitHubCopilotProvider.Id] = "per-provider-model" },
        };

        var (provider, model) = ProviderModelResolver.Resolve(null, null, settings, connectedProviderId: GitHubCopilotProvider.Id);

        Assert.Equal(GitHubCopilotProvider.Id, provider);
        Assert.Equal("per-provider-model", model);
    }

    [Fact]
    public void PerProviderModel_DoesNotLeakToAnotherProvider()
    {
        // A model set for Copilot must NOT be used when the connected provider is Claude.ai —
        // it falls back to Claude's own built-in default instead.
        var settings = CodaSettings.Empty with
        {
            DefaultModelByProvider = new Dictionary<string, string> { [GitHubCopilotProvider.Id] = "copilot-only-model" },
        };

        var (provider, model) = ProviderModelResolver.Resolve(null, null, settings, connectedProviderId: ClaudeAiProvider.Id);

        Assert.Equal(ClaudeAiProvider.Id, provider);
        Assert.NotEqual("copilot-only-model", model);
        Assert.Equal(ProviderDefaults.ModelFor(ClaudeAiProvider.Id), model);
    }

    [Fact]
    public void ResolvedProvider_NoModelConfigured_FallsBackToProviderBuiltInDefault()
    {
        var (provider, model) = ProviderModelResolver.Resolve(null, null, CodaSettings.Empty, connectedProviderId: GitHubCopilotProvider.Id);

        Assert.Equal(GitHubCopilotProvider.Id, provider);
        Assert.False(string.IsNullOrWhiteSpace(model));
        Assert.Equal(ProviderDefaults.ModelFor(GitHubCopilotProvider.Id), model);
    }

    [Fact]
    public void Flag_WinsOverPerProviderModel()
    {
        var settings = CodaSettings.Empty with
        {
            DefaultModelByProvider = new Dictionary<string, string> { [GitHubCopilotProvider.Id] = "per-provider-model" },
        };

        var (_, model) = ProviderModelResolver.Resolve(null, modelFlag: "flag-model", settings, connectedProviderId: GitHubCopilotProvider.Id);

        Assert.Equal("flag-model", model);
    }
}
