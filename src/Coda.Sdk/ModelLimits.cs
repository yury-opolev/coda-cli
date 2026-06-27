namespace Coda.Sdk;

/// <summary>
/// Resolves a model's per-response output-token ceiling (the request's <c>max_tokens</c>) from the
/// model catalog, so the value sent is the model's REAL published limit rather than a flat default.
/// This matters because the Anthropic API returns HTTP 400 when <c>max_tokens</c> exceeds the model's
/// output cap (it does NOT clamp), and providers cap differently for the same family — e.g. GitHub
/// Copilot's claude-sonnet-4 caps output at 16000 while the direct Anthropic claude-sonnet-4-6 allows
/// 64000. A flat default would 400 the smaller ones and truncate the larger ones.
/// </summary>
public static class ModelLimits
{
    /// <summary>
    /// Conservative last-resort output ceiling, used ONLY when the catalog has no entry for the model
    /// (e.g. an unmapped local provider). The bundled catalog covers all Anthropic and GitHub Copilot
    /// models, so this rarely applies — the real per-model limit is authoritative.
    /// </summary>
    public const int FallbackMaxOutputTokens = 8192;

    /// <summary>
    /// The effective <c>max_tokens</c> for a turn: the model's published output limit when known. An
    /// explicit <paramref name="cap"/> (a per-session override) is honored but never exceeds the model's
    /// real ceiling (so it can never produce a 400). Falls back to <see cref="FallbackMaxOutputTokens"/>
    /// only when the model is absent from the catalog.
    /// </summary>
    public static int ResolveMaxOutputTokens(ModelCatalog catalog, string providerId, string model, int? cap)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var modelLimit = catalog.Get(providerId, model)?.OutputLimit;

        if (cap is int requested)
        {
            // Explicit override — honored, but clamped to the model's real ceiling when known.
            return modelLimit is int limit ? Math.Min(requested, limit) : requested;
        }

        // Default: the model's real output ceiling; conservative fallback only for an unknown model.
        return modelLimit ?? FallbackMaxOutputTokens;
    }

    /// <summary>Floor for the auto-compaction threshold so tiny/unknown windows still leave room to work.</summary>
    public const int MinAutoCompactThreshold = 8_000;

    /// <summary>
    /// The token count at which history auto-compaction triggers, derived from the MODEL'S real
    /// context window rather than a flat constant: <c>contextWindow - outputReserve</c> (the usable
    /// input budget, mirroring opencode's overflow check). This scales correctly — a 1M-window model
    /// holds far more than a 200k one, and a small window compacts early enough to avoid overflow.
    /// An explicit <paramref name="configured"/> value (&gt; 0) is honored as an override; 0 means
    /// "derive from the window".
    /// </summary>
    public static int ResolveAutoCompactThreshold(ModelCatalog catalog, string providerId, string model, int configured)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (configured > 0)
        {
            return configured;
        }

        var entry = catalog.Get(providerId, model);
        var context = entry?.ContextLimit ?? Coda.Common.ContextWindow.DefaultTokens;
        var reserve = entry?.OutputLimit ?? FallbackMaxOutputTokens;
        return Math.Max(MinAutoCompactThreshold, context - reserve);
    }
}
