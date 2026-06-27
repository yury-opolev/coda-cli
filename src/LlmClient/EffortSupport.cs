namespace LlmClient;

/// <summary>
/// Effort-level support rules for Anthropic models, mirroring the reference
/// client's <c>utils/effort.ts</c>. Effort controls how long the model reasons
/// before answering; it is sent as <c>output_config.effort</c> with the
/// <c>anthropic-beta: effort-2025-11-24</c> header and is only honored by a
/// subset of Claude 4 models. Copilot (OpenAI-shaped) has no equivalent.
/// </summary>
public static class EffortSupport
{
    /// <summary>The beta header value gating the <c>output_config.effort</c> parameter.</summary>
    public const string EffortBetaHeader = "effort-2025-11-24";

    /// <summary>Valid effort levels, lowest to highest.</summary>
    public static readonly IReadOnlyList<string> Levels = ["low", "medium", "high", "max"];

    /// <summary>True when <paramref name="value"/> is one of <see cref="Levels"/>.</summary>
    public static bool IsEffortLevel(string? value) =>
        value is not null && Levels.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the model accepts the effort parameter. Per the reference, a subset
    /// of Claude 4 models (opus-4-8, sonnet-4-6); explicitly excludes haiku and
    /// older opus/sonnet variants.
    /// </summary>
    public static bool ModelSupportsEffort(string model)
    {
        var m = (model ?? string.Empty).ToLowerInvariant();
        if (m.Contains("opus-4-8") || m.Contains("sonnet-4-6") || m.Contains("opus-4-6"))
        {
            return true;
        }

        return false;
    }

    /// <summary>Whether the model accepts <c>max</c> effort (Opus only, per the API).</summary>
    public static bool ModelSupportsMaxEffort(string model)
    {
        var m = (model ?? string.Empty).ToLowerInvariant();
        return m.Contains("opus-4-8") || m.Contains("opus-4-6");
    }

    /// <summary>
    /// Resolve the effort value actually sent to the API for a model. Returns
    /// <c>null</c> when nothing should be sent (no effort chosen, or the model
    /// doesn't support effort). <c>max</c> is downgraded to <c>high</c> on models
    /// that don't support max effort, matching the reference clamp.
    /// </summary>
    public static string? ResolveAppliedEffort(string model, string? requested)
    {
        if (string.IsNullOrEmpty(requested) || !IsEffortLevel(requested))
        {
            return null;
        }

        if (!ModelSupportsEffort(model))
        {
            return null;
        }

        var level = requested.ToLowerInvariant();
        if (level == "max" && !ModelSupportsMaxEffort(model))
        {
            return "high";
        }

        return level;
    }
}
