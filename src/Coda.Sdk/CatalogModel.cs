namespace Coda.Sdk;

/// <summary>
/// Metadata about a model from the catalog (models.dev): a display name, the
/// context-window size, the per-response output-token ceiling, and per-million-token
/// pricing (USD). Any field may be null when the catalog doesn't carry it.
/// </summary>
public sealed record CatalogModel(
    string Id,
    string? DisplayName = null,
    int? ContextLimit = null,
    int? OutputLimit = null,
    decimal? InputPerMTok = null,
    decimal? OutputPerMTok = null,
    decimal? CacheReadPerMTok = null,
    decimal? CacheWritePerMTok = null);
