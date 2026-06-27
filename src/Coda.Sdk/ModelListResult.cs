namespace Coda.Sdk;

/// <summary>Where a model list came from, best (most authoritative) first.</summary>
public enum ModelSource
{
    /// <summary>The provider's live model-listing endpoint (real entitlements).</summary>
    Live,

    /// <summary>The models.dev catalog (bundled snapshot / on-disk cache).</summary>
    Catalog,

    /// <summary>A last-resort built-in list (only when live and catalog are both unavailable).</summary>
    BuiltIn,
}

/// <summary>One model in a resolved list, with catalog metadata when known.</summary>
public sealed record ModelListEntry(string Id, string? DisplayName = null, int? ContextLimit = null);

/// <summary>A resolved model list for a provider, plus where it came from.</summary>
public sealed record ModelListResult(
    string ProviderId,
    ModelSource Source,
    IReadOnlyList<ModelListEntry> Models);
