namespace LlmClient;

/// <summary>
/// Resolves the <see cref="ReasoningCapability"/> for a (provider, model) pair.
/// Replaces <see cref="EffortSupport"/> as the public entry point for
/// reasoning-effort capability queries and applied-level resolution.
/// </summary>
/// <remarks>
/// Anthropic models use static rules documented by the API (which models accept effort
/// and which levels). Copilot/OpenAI models read their supported levels from the
/// provider's model-metadata (<c>capabilities.supports.reasoning_effort</c>); missing
/// metadata is treated as not supported (safe default).
/// </remarks>
public static class ReasoningCapabilityResolver
{
    private const string CopilotProviderId = "github-copilot";

    /// <summary>Valid levels for Anthropic Opus 4.x (accepts all four levels including max).</summary>
    private static readonly IReadOnlyList<string> AnthropicOpusLevels = ["low", "medium", "high", "max"];

    /// <summary>
    /// Valid levels for Anthropic Sonnet 4.6 (<c>max</c> is not accepted natively;
    /// it is clamped to <c>high</c> on the wire).
    /// </summary>
    private static readonly IReadOnlyList<string> AnthropicSonnetLevels = ["low", "medium", "high"];

    /// <summary>
    /// Resolves the reasoning capability for a given (provider, model) pair.
    /// </summary>
    /// <param name="providerId">
    /// The provider identifier (e.g. <c>"github-copilot"</c>, <c>"claude-ai"</c>,
    /// <c>"anthropic-api-key"</c>). Only <c>"github-copilot"</c> triggers
    /// Copilot/OpenAI rules; all other values use Anthropic static rules.
    /// </param>
    /// <param name="model">The model identifier (e.g. <c>"claude-opus-4.8"</c>).</param>
    /// <param name="reasoningLevels">
    /// For Copilot/OpenAI models: the effort levels from
    /// <c>capabilities.supports.reasoning_effort</c> in the provider's model metadata.
    /// Null or empty means the model does not support reasoning effort.
    /// </param>
    public static ReasoningCapability Resolve(
        string providerId,
        string model,
        IReadOnlyList<string>? reasoningLevels = null)
    {
        if (string.Equals(providerId, CopilotProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveCopilot(reasoningLevels);
        }

        return ResolveAnthropic(model);
    }

    /// <summary>
    /// Resolves the reasoning capability for an Anthropic (Claude) model using static
    /// API rules, regardless of provider (for use in Anthropic-only clients).
    /// </summary>
    public static ReasoningCapability ResolveAnthropic(string model)
    {
        var m = (model ?? string.Empty).ToLowerInvariant();

        if (m.Contains("opus-4-8") || m.Contains("opus-4.8"))
        {
            return new ReasoningCapability(
                Supported: true,
                Levels: AnthropicOpusLevels,
                SupportsAuto: true,
                Delivery: ReasoningDelivery.AnthropicEffort);
        }

        if (m.Contains("opus-4-6") || m.Contains("opus-4.6"))
        {
            return new ReasoningCapability(
                Supported: true,
                Levels: AnthropicOpusLevels,
                SupportsAuto: true,
                Delivery: ReasoningDelivery.AnthropicEffort);
        }

        if (m.Contains("sonnet-4-6") || m.Contains("sonnet-4.6"))
        {
            return new ReasoningCapability(
                Supported: true,
                Levels: AnthropicSonnetLevels,
                SupportsAuto: true,
                Delivery: ReasoningDelivery.AnthropicEffort);
        }

        return ReasoningCapability.Unsupported;
    }

    /// <summary>
    /// Resolves the level actually sent to the API for a given capability and
    /// requested level. Returns <c>null</c> when nothing should be sent: the model
    /// is unsupported, "auto"/null/empty was requested, or the requested level is
    /// not in the capability's list (and no clamp applies). Applies the Anthropic
    /// <c>max</c>→<c>high</c> clamp when <c>max</c> is requested but the model's
    /// Anthropic capability does not list it (e.g. Sonnet 4.6).
    /// </summary>
    public static string? ResolveAppliedLevel(ReasoningCapability capability, string? requested)
    {
        if (!capability.Supported)
        {
            return null;
        }

        if (string.IsNullOrEmpty(requested) ||
            string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var level = requested.ToLowerInvariant();

        // Anthropic max→high clamp: if max is requested but the capability's level list
        // doesn't include max (e.g. Sonnet 4.6), downgrade to high per the API rule.
        if (capability.Delivery == ReasoningDelivery.AnthropicEffort
            && level == "max"
            && !capability.Levels.Contains("max", StringComparer.OrdinalIgnoreCase))
        {
            return "high";
        }

        return capability.Levels.Contains(level, StringComparer.OrdinalIgnoreCase) ? level : null;
    }

    private static ReasoningCapability ResolveCopilot(IReadOnlyList<string>? reasoningLevels)
    {
        if (reasoningLevels is { Count: > 0 })
        {
            return new ReasoningCapability(
                Supported: true,
                Levels: reasoningLevels,
                SupportsAuto: true,
                Delivery: ReasoningDelivery.OpenAiResponses);
        }

        return ReasoningCapability.Unsupported;
    }
}
