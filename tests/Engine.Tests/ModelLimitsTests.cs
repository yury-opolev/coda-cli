using Coda.Sdk;
using LlmAuth.Providers.ClaudeAi;

namespace Engine.Tests;

public sealed class ModelLimitsTests
{
    private static ModelCatalog TestCatalog() => new(new Dictionary<string, IReadOnlyDictionary<string, CatalogModel>>
    {
        ["anthropic"] = new Dictionary<string, CatalogModel>
        {
            ["claude-sonnet-4-6"] = new CatalogModel("claude-sonnet-4-6", OutputLimit: 64000),
            ["claude-opus-4-8"] = new CatalogModel("claude-opus-4-8", OutputLimit: 128000),
        },
    });

    [Fact]
    public void Resolves_to_the_models_published_output_limit()
    {
        Assert.Equal(64000, ModelLimits.ResolveMaxOutputTokens(TestCatalog(), ClaudeAiProvider.Id, "claude-sonnet-4-6", cap: null));
        Assert.Equal(128000, ModelLimits.ResolveMaxOutputTokens(TestCatalog(), ClaudeAiProvider.Id, "claude-opus-4-8", cap: null));
    }

    [Fact]
    public void Caps_an_explicit_override_to_the_model_ceiling()
    {
        // An override above the model's real cap is clamped down (it would otherwise 400 on Anthropic).
        Assert.Equal(64000, ModelLimits.ResolveMaxOutputTokens(TestCatalog(), ClaudeAiProvider.Id, "claude-sonnet-4-6", cap: 999_999));
    }

    [Fact]
    public void Honors_an_explicit_override_below_the_model_ceiling()
    {
        Assert.Equal(8000, ModelLimits.ResolveMaxOutputTokens(TestCatalog(), ClaudeAiProvider.Id, "claude-sonnet-4-6", cap: 8000));
    }

    [Fact]
    public void Falls_back_only_for_an_unknown_model()
    {
        Assert.Equal(
            ModelLimits.FallbackMaxOutputTokens,
            ModelLimits.ResolveMaxOutputTokens(TestCatalog(), ClaudeAiProvider.Id, "some-unlisted-model", cap: null));
    }

    private static ModelCatalog WindowCatalog() => new(new Dictionary<string, IReadOnlyDictionary<string, CatalogModel>>
    {
        ["anthropic"] = new Dictionary<string, CatalogModel>
        {
            ["claude-opus-4-8"] = new CatalogModel("claude-opus-4-8", ContextLimit: 1_000_000, OutputLimit: 128_000),
        },
    });

    [Fact]
    public void AutoCompactThreshold_derives_from_the_models_context_window()
    {
        // Window-relative: usable input budget = contextWindow - outputReserve (scales with the model).
        Assert.Equal(
            1_000_000 - 128_000,
            ModelLimits.ResolveAutoCompactThreshold(WindowCatalog(), ClaudeAiProvider.Id, "claude-opus-4-8", configured: 0));
    }

    [Fact]
    public void AutoCompactThreshold_honors_an_explicit_override()
    {
        Assert.Equal(
            50_000,
            ModelLimits.ResolveAutoCompactThreshold(WindowCatalog(), ClaudeAiProvider.Id, "claude-opus-4-8", configured: 50_000));
    }

    [Fact]
    public void AutoCompactThreshold_floors_for_an_unknown_model()
    {
        var resolved = ModelLimits.ResolveAutoCompactThreshold(WindowCatalog(), ClaudeAiProvider.Id, "unknown", configured: 0);
        Assert.True(resolved >= ModelLimits.MinAutoCompactThreshold);
    }

    [Fact]
    public void Bundled_catalog_carries_real_output_limits()
    {
        // Guards the snapshot regeneration + parser: the offline default must carry real output limits,
        // otherwise we'd silently fall back to a flat value (the bug this whole change fixes). Uses the
        // BUNDLED catalog explicitly — asserting against Default would read the machine's ~/.coda cache
        // and drift when a model's real limit changes upstream.
        Assert.Equal(64000, ModelLimits.ResolveMaxOutputTokens(ModelCatalog.Bundled, ClaudeAiProvider.Id, "claude-sonnet-4-6", cap: null));
        Assert.Equal(128000, ModelLimits.ResolveMaxOutputTokens(ModelCatalog.Bundled, ClaudeAiProvider.Id, "claude-opus-4-8", cap: null));
    }
}
