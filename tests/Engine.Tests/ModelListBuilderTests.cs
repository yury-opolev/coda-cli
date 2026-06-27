using Coda.Sdk;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;

namespace Engine.Tests;

public sealed class ModelListBuilderTests
{
    private static ModelCatalog Catalog() => new(ModelCatalog.TryParse("""
        { "anthropic": { "models": {
          "claude-opus-4-8": { "name": "Claude Opus 4.8", "limit": { "context": 200000 } },
          "claude-sonnet-4-6": { "name": "Claude Sonnet 4.6", "limit": { "context": 1000000 } }
        } } }
        """)!);

    [Fact]
    public void Live_list_wins_and_is_enriched_from_catalog()
    {
        var result = ModelListBuilder.Build(
            ClaudeAiProvider.Id,
            [new ModelInfo("claude-opus-4-8")],
            Catalog());

        Assert.Equal(ModelSource.Live, result.Source);
        var entry = Assert.Single(result.Models);
        Assert.Equal("claude-opus-4-8", entry.Id);
        Assert.Equal("Claude Opus 4.8", entry.DisplayName); // enriched
        Assert.Equal(200000, entry.ContextLimit);
    }

    [Fact]
    public void Live_context_limit_overrides_catalog()
    {
        // The catalog says claude-opus-4-8 is 200000, but the live list reports 1M
        // (e.g. an internal Copilot "-1m" model) — the live value must win.
        var result = ModelListBuilder.Build(
            ClaudeAiProvider.Id,
            [new ModelInfo("claude-opus-4-8", "Opus 1M (internal)", ContextLimit: 1_000_000)],
            Catalog());

        var entry = Assert.Single(result.Models);
        Assert.Equal(1_000_000, entry.ContextLimit);
        Assert.Equal("Opus 1M (internal)", entry.DisplayName);
    }

    [Fact]
    public void Empty_live_falls_back_to_catalog_list()
    {
        var result = ModelListBuilder.Build(ClaudeAiProvider.Id, [], Catalog());

        Assert.Equal(ModelSource.Catalog, result.Source);
        Assert.Equal(2, result.Models.Count);
        Assert.Contains(result.Models, m => m.Id == "claude-sonnet-4-6" && m.ContextLimit == 1000000);
    }

    [Fact]
    public void ResolveContextWindow_prefers_live_over_catalog_then_const()
    {
        var catalog = Catalog(); // claude-opus-4-8 = 200000

        // Live wins (e.g. an internal Copilot -1m model the catalog mis-sizes at 200k).
        var live = new[] { new ModelInfo("claude-opus-4-8", null, 1_000_000) };
        Assert.Equal(1_000_000, CodaSession.ResolveContextWindow(live, ClaudeAiProvider.Id, "claude-opus-4-8", catalog));

        // No live → catalog.
        Assert.Equal(200_000, CodaSession.ResolveContextWindow([], ClaudeAiProvider.Id, "claude-opus-4-8", catalog));

        // Neither → nominal default.
        Assert.Equal(CodaSession.ContextWindowTokens, CodaSession.ResolveContextWindow([], ClaudeAiProvider.Id, "unknown-x", catalog));
    }

    [Fact]
    public void Empty_live_and_empty_catalog_falls_back_to_builtin()
    {
        var emptyCatalog = new ModelCatalog(new Dictionary<string, IReadOnlyDictionary<string, CatalogModel>>());

        var result = ModelListBuilder.Build(ClaudeAiProvider.Id, [], emptyCatalog);

        Assert.Equal(ModelSource.BuiltIn, result.Source);
        Assert.NotEmpty(result.Models);
        Assert.All(result.Models, m => Assert.Contains(m.Id, AnthropicModels.Known));
    }
}
