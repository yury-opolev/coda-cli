using System.Net;
using System.Text;
using Coda.Sdk;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests;

public sealed class ModelCatalogTests
{
    private const string Json = """
        {
          "anthropic": { "models": {
            "claude-opus-4-8": { "name": "Claude Opus 4.8", "limit": { "context": 200000 },
              "cost": { "input": 5, "output": 25, "cache_read": 0.5, "cache_write": 6.25 } },
            "claude-haiku-4-5-20251001": { "name": "Claude Haiku 4.5", "limit": { "context": 200000 },
              "cost": { "input": 0.8, "output": 4 } }
          } },
          "github-copilot": { "models": {
            "gpt-4o": { "name": "GPT-4o", "limit": { "context": 128000 } }
          } },
          "openai": { "models": { "ignored": { "name": "Ignored" } } }
        }
        """;

    private static ModelCatalog Build() => new(ModelCatalog.TryParse(Json)!);

    [Fact]
    public void ProviderKeyFor_maps_coda_providers_to_modelsdev_keys()
    {
        Assert.Equal("anthropic", ModelCatalog.ProviderKeyFor(ClaudeAiProvider.Id));
        Assert.Equal("anthropic", ModelCatalog.ProviderKeyFor(ApiKeyProvider.Id));
        Assert.Equal("github-copilot", ModelCatalog.ProviderKeyFor(GitHubCopilotProvider.Id));
        Assert.Null(ModelCatalog.ProviderKeyFor("unknown-provider"));
    }

    [Fact]
    public void TryParse_reads_name_context_and_cost()
    {
        var catalog = Build();

        var opus = catalog.Get(ClaudeAiProvider.Id, "claude-opus-4-8");
        Assert.NotNull(opus);
        Assert.Equal("Claude Opus 4.8", opus!.DisplayName);
        Assert.Equal(200000, opus.ContextLimit);
        Assert.Equal(5m, opus.InputPerMTok);
        Assert.Equal(25m, opus.OutputPerMTok);
        Assert.Equal(0.5m, opus.CacheReadPerMTok);
    }

    [Fact]
    public void Get_matches_alias_to_versioned_id()
    {
        var catalog = Build();

        // The Coda alias "claude-haiku-4-5" should resolve to the dated catalog id.
        var haiku = catalog.Get(ApiKeyProvider.Id, "claude-haiku-4-5");
        Assert.NotNull(haiku);
        Assert.Equal("Claude Haiku 4.5", haiku!.DisplayName);
    }

    [Fact]
    public void Get_returns_null_for_unmapped_provider_or_unknown_model()
    {
        var catalog = Build();
        Assert.Null(catalog.Get("unknown-provider", "claude-opus-4-8"));
        Assert.Null(catalog.Get(ClaudeAiProvider.Id, "no-such-model"));
    }

    [Fact]
    public void TryParse_ignores_unmapped_providers()
    {
        var catalog = Build();
        // "openai" is present in the JSON but not a provider Coda maps.
        Assert.Empty(catalog.ForProvider("openai"));
        Assert.NotEmpty(catalog.ForProvider(ClaudeAiProvider.Id));
    }

    [Fact]
    public void TryParse_returns_null_on_garbage()
    {
        Assert.Null(ModelCatalog.TryParse("not json"));
    }

    [Fact]
    public void Default_loads_bundled_snapshot()
    {
        // The embedded snapshot must be wired and contain known models with metadata.
        var sonnet = ModelCatalog.Default.Get(ClaudeAiProvider.Id, "claude-sonnet-4-6");
        Assert.NotNull(sonnet);
        Assert.NotNull(sonnet!.ContextLimit);
        Assert.True(sonnet.InputPerMTok > 0);
    }

    [Fact]
    public void Get_alias_resolves_to_closest_versioned_id()
    {
        const string json = """
            { "anthropic": { "models": {
              "claude-haiku-4-5": { "name": "alias" },
              "claude-haiku-4-5-20251001": { "name": "dated" }
            } } }
            """;
        var catalog = new ModelCatalog(ModelCatalog.TryParse(json)!);

        // Exact wins.
        Assert.Equal("alias", catalog.Get(ClaudeAiProvider.Id, "claude-haiku-4-5")!.DisplayName);
        // A versioned query with only a shorter alias present resolves to the alias.
        const string json2 = """{ "anthropic": { "models": { "claude-haiku-4-5": { "name": "alias" } } } }""";
        var catalog2 = new ModelCatalog(ModelCatalog.TryParse(json2)!);
        Assert.Equal("alias", catalog2.Get(ClaudeAiProvider.Id, "claude-haiku-4-5-20251001")!.DisplayName);
    }

    private sealed class JsonHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task RefreshAsync_writes_valid_catalog_to_destination()
    {
        var dest = Path.Combine(Path.GetTempPath(), "coda_models_" + Guid.NewGuid().ToString("N"), "models.json");
        try
        {
            using var http = new HttpClient(new JsonHandler(HttpStatusCode.OK, Json));
            var ok = await ModelCatalog.RefreshAsync(http, dest);

            Assert.True(ok);
            Assert.True(File.Exists(dest));
            var reloaded = new ModelCatalog(ModelCatalog.TryParse(File.ReadAllText(dest))!);
            Assert.NotNull(reloaded.Get(ClaudeAiProvider.Id, "claude-opus-4-8"));
        }
        finally
        {
            var dir = Path.GetDirectoryName(dest)!;
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task RefreshAsync_returns_false_and_writes_nothing_on_garbage()
    {
        var dest = Path.Combine(Path.GetTempPath(), "coda_models_" + Guid.NewGuid().ToString("N"), "models.json");
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.OK, "<<not json>>"));

        var ok = await ModelCatalog.RefreshAsync(http, dest);

        Assert.False(ok);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void IsStale_true_when_missing_and_false_when_fresh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "coda_stale_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "models.json");
        try
        {
            Assert.True(ModelCatalog.IsStale(file, TimeSpan.FromHours(1))); // missing → stale

            File.WriteAllText(file, "{}");
            Assert.False(ModelCatalog.IsStale(file, TimeSpan.FromHours(1))); // just written → fresh

            File.SetLastWriteTimeUtc(file, DateTime.UtcNow - TimeSpan.FromHours(2));
            Assert.True(ModelCatalog.IsStale(file, TimeSpan.FromHours(1))); // aged out → stale
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Pricing_prefers_catalog_rates_then_falls_back()
    {
        var usage = new TokenUsage(InputTokens: 1_000_000, OutputTokens: 1_000_000);

        // Catalog rates: 5 in / 25 out → 30.
        var withCatalog = Pricing.EstimateUsd("whatever", usage,
            new CatalogModel("x", InputPerMTok: 5m, OutputPerMTok: 25m));
        Assert.Equal(30m, withCatalog);

        // No catalog → built-in table (opus = 15 in / 75 out → 90).
        var fallback = Pricing.EstimateUsd("claude-opus-4-8", usage, catalog: null);
        Assert.Equal(90m, fallback);
    }
}
