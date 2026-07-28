namespace LlmClient;

/// <summary>
/// Per-model minimum cacheable prefix size in tokens.
/// </summary>
/// <remarks>
/// <para>
/// A below-minimum prefix is silently processed uncached — the API returns no error,
/// and both cache counters stay at zero. This must be per-model rather than a tier constant:
/// Haiku 4.5 requires 4 096 tokens while Opus 5 requires only 512, so any single threshold
/// would either miss small Opus 5 opportunities or waste writes on sub-minimum Haiku 4.5
/// requests.
/// </para>
/// <para>
/// Source: <c>platform.claude.com/docs/en/build-with-claude/prompt-caching</c> (verified 2026-07-28).
/// </para>
/// </remarks>
public static class CacheMinimumPrefix
{
    /// <summary>Default minimum for models not in the known table (1 024 tokens).</summary>
    public const int Default = 1024;

    /// <summary>
    /// Returns the minimum cacheable prefix in tokens for the given model ID.
    /// Matching is loose: <c>Contains</c> on the lower-cased ID, checked most-specific first.
    /// Unknown models return <see cref="Default"/>.
    /// </summary>
    public static int For(string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return Default;
        }

        // Normalise to dashed form so both spellings (e.g. "opus-4-6" and "opus-4.6") resolve
        // identically. The model catalog ships both forms: Anthropic-direct uses dashes, Copilot
        // uses dots. Replacing '.' with '-' once here avoids duplicating every match arm.
        var lower = model.ToLowerInvariant().Replace('.', '-');

        // 512-token tier: Opus 5, Fable 5, Mythos 5
        if (lower.Contains("opus-5") || lower.Contains("fable-5") || lower.Contains("mythos-5"))
        {
            return 512;
        }

        // 4 096-token tier: Opus 4.6, Opus 4.5, Haiku 4.5
        // Checked before the 2 048 bucket so "opus-4-5" is not shadowed by a weaker match.
        if (lower.Contains("opus-4-6") || lower.Contains("opus-4-5") || lower.Contains("haiku-4-5"))
        {
            return 4096;
        }

        // 2 048-token tier: Mythos Preview, Opus 4.7, Haiku 3.5
        if (lower.Contains("opus-4-7") || lower.Contains("haiku-3-5") || lower.Contains("mythos-preview"))
        {
            return 2048;
        }

        // Default 1 024: Opus 4.8, Sonnet 5, Sonnet 4.6, Sonnet 4.5, Opus 4.1, Opus 4, Sonnet 4,
        // and all unknown models.
        return Default;
    }
}
