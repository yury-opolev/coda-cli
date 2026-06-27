using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Coda.Sdk;

/// <summary>
/// Resolves the model list shown to users from the best available source, so every
/// front-end (TUI, headless, serve) presents the same thing. Order:
/// <list type="number">
///   <item>the provider's live list (real entitlements), when non-empty;</item>
///   <item>the models.dev <see cref="ModelCatalog"/> for the provider (bundled/cached);</item>
///   <item>a last-resort built-in list (only if the catalog is also empty).</item>
/// </list>
/// Every entry is annotated with display name + context window from the catalog.
/// </summary>
public static class ModelListBuilder
{
    public static ModelListResult Build(string providerId, IReadOnlyList<ModelInfo> live, ModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(catalog);

        if (live.Count > 0)
        {
            return new ModelListResult(providerId, ModelSource.Live, [.. live.Select(m => Enrich(providerId, m, catalog))]);
        }

        var catalogModels = catalog.ForProvider(providerId);
        if (catalogModels.Count > 0)
        {
            return new ModelListResult(
                providerId,
                ModelSource.Catalog,
                [.. catalogModels.Select(m => new ModelListEntry(m.Id, m.DisplayName, m.ContextLimit))]);
        }

        // Last resort: the handcrafted list, only when nothing better exists.
        var builtIn = providerId == GitHubCopilotProvider.Id ? CopilotModels.Known : AnthropicModels.Known;
        return new ModelListResult(providerId, ModelSource.BuiltIn, [.. builtIn.Select(id => Enrich(providerId, new ModelInfo(id), catalog))]);
    }

    private static ModelListEntry Enrich(string providerId, ModelInfo live, ModelCatalog catalog)
    {
        var meta = catalog.Get(providerId, live.Id);
        // Prefer values the provider reported live (authoritative for entitlements and
        // for special/internal models the catalog doesn't know), then the catalog.
        return new ModelListEntry(
            live.Id,
            live.DisplayName ?? meta?.DisplayName,
            live.ContextLimit ?? meta?.ContextLimit);
    }
}
