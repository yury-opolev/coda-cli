using LlmClient;

namespace Coda.Sdk;

/// <summary>Rough public-price table for estimating USD cost of token usage.</summary>
public static class Pricing
{
    // (inputPerMillionTokens, outputPerMillionTokens) in USD
    private static readonly (decimal In, decimal Out) SonnetPricing = (3.00m, 15.00m);
    private static readonly (decimal In, decimal Out) OpusPricing = (15.00m, 75.00m);
    private static readonly (decimal In, decimal Out) HaikuPricing = (0.80m, 4.00m);

    private const decimal CacheReadMultiplier = 0.10m;
    private const decimal CacheWrite5mMultiplier = 1.25m;
    private const decimal CacheWrite1hMultiplier = 2.00m;

    /// <summary>Returns (inPerMTok, outPerMTok) in USD for the given model, defaulting to Sonnet pricing.</summary>
    public static (decimal InPerMTok, decimal OutPerMTok) For(string model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return SonnetPricing;
        }

        var lower = model.ToLowerInvariant();

        if (lower.Contains("opus"))
        {
            return OpusPricing;
        }

        if (lower.Contains("haiku"))
        {
            return HaikuPricing;
        }

        // sonnet (default for any unknown / claude-sonnet-* / gpt-* etc.)
        return SonnetPricing;
    }

    /// <summary>Estimates the USD cost for the given usage and model.</summary>
    public static decimal EstimateUsd(string model, TokenUsage usage) =>
        EstimateUsd(model, usage, catalog: null);

    /// <summary>
    /// Estimates the USD cost, preferring catalog pricing (models.dev) when present
    /// and falling back to the built-in price table per rate that is missing.
    /// Cache reads are billed at <c>cacheReadRate</c> (catalog's <c>CacheReadPerMTok</c> when supplied,
    /// otherwise <c>baseInRate * 0.10</c>). Cache writes use the catalog's <c>CacheWritePerMTok</c>
    /// for the 5-minute tier, and <c>baseInRate * 2.00</c> for the 1-hour tier (the catalog carries
    /// only one write rate). Uncached input tokens are billed at the base input rate.
    /// </summary>
    public static decimal EstimateUsd(string model, TokenUsage usage, CatalogModel? catalog)
    {
        var inRate = catalog?.InputPerMTok;
        var outRate = catalog?.OutputPerMTok;
        if (inRate is null || outRate is null)
        {
            var (fallbackIn, fallbackOut) = For(model);
            inRate ??= fallbackIn;
            outRate ??= fallbackOut;
        }

        var baseInRate = inRate.Value;
        var cacheReadRate = catalog?.CacheReadPerMTok ?? baseInRate * CacheReadMultiplier;
        var cacheWrite5mRate = catalog?.CacheWritePerMTok ?? baseInRate * CacheWrite5mMultiplier;
        var cacheWrite1hRate = baseInRate * CacheWrite1hMultiplier;

        return (baseInRate * usage.InputTokens
            + cacheReadRate * usage.CacheReadTokens
            + cacheWrite5mRate * usage.CacheWrite5mTokens
            + cacheWrite1hRate * usage.CacheWrite1hTokens
            + outRate.Value * usage.OutputTokens)
            / 1_000_000m;
    }
}
