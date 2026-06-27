using Coda.Sdk;
using Coda.Tui.Repl;

namespace Coda.Tui.Tests;

public sealed class ModelListCommandTests
{
    [Fact]
    public async Task Model_without_args_falls_back_to_catalog_when_live_unavailable()
    {
        // No credentials → no live list → the models.dev catalog (bundled snapshot)
        // is used, not the handcrafted built-in list.
        var (app, context, console, _) = TestAppBuilder.BuildApp();

        await app.DispatchAsync(ParsedInput.Slash("model", Array.Empty<string>()), CancellationToken.None);

        Assert.Contains("models.dev catalog", console.Output);
        Assert.Contains("claude-sonnet-4-6", console.Output); // a real catalog model
        var cached = context.Session.ModelListCache[context.Session.ActiveProviderId];
        Assert.Equal(ModelSource.Catalog, cached.Source);
    }

    [Fact]
    public async Task Model_without_args_uses_cached_result()
    {
        var (app, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.ModelListCache[context.Session.ActiveProviderId] =
            new ModelListResult(context.Session.ActiveProviderId, ModelSource.Live,
                [new ModelListEntry("x-live-model", "X Live", 123456)]);

        await app.DispatchAsync(ParsedInput.Slash("model", Array.Empty<string>()), CancellationToken.None);

        Assert.Contains("x-live-model", console.Output);
        Assert.Contains("live", console.Output);
    }

    [Fact]
    public async Task Model_refresh_clears_cache_and_recomputes()
    {
        // Keep the test offline: /model refresh would otherwise fetch models.dev.
        var prior = Environment.GetEnvironmentVariable("CODA_DISABLE_MODELS_FETCH");
        Environment.SetEnvironmentVariable("CODA_DISABLE_MODELS_FETCH", "1");
        try
        {
            var (app, context, _, _) = TestAppBuilder.BuildApp();
            var providerId = context.Session.ActiveProviderId;
            context.Session.ModelListCache[providerId] =
                new ModelListResult(providerId, ModelSource.Live, [new ModelListEntry("x-live-model")]);

            await app.DispatchAsync(ParsedInput.Slash("model", new[] { "refresh" }), CancellationToken.None);

            // Stale entry replaced; unsigned → recomputed from catalog.
            var cached = context.Session.ModelListCache[providerId];
            Assert.DoesNotContain(cached.Models, m => m.Id == "x-live-model");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODA_DISABLE_MODELS_FETCH", prior);
        }
    }

    [Fact]
    public async Task Model_with_arg_still_sets_model_without_listing()
    {
        var (app, context, _, _) = TestAppBuilder.BuildApp();

        await app.DispatchAsync(ParsedInput.Slash("model", new[] { "claude-opus-4-8" }), CancellationToken.None);

        Assert.Equal("claude-opus-4-8", context.Session.Model);
        Assert.False(context.Session.ModelListCache.ContainsKey(context.Session.ActiveProviderId));
    }
}
