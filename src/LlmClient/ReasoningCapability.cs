namespace LlmClient;

/// <summary>How a chosen reasoning effort level is delivered in the wire request.</summary>
public enum ReasoningDelivery
{
    /// <summary>Model does not support reasoning effort; nothing is sent.</summary>
    None,

    /// <summary>
    /// Anthropic-native delivery: <c>output_config.effort</c> with the
    /// <c>anthropic-beta: effort-2025-11-24</c> request header.
    /// </summary>
    AnthropicEffort,

    /// <summary>
    /// OpenAI/Copilot Responses-API delivery: <c>reasoning.effort</c> sent on the
    /// <c>/responses</c> endpoint.
    /// </summary>
    OpenAiResponses,
}

/// <summary>
/// Per-(provider, model) reasoning capability: whether the model supports reasoning
/// effort, which levels are valid, whether "auto" (no explicit level) is allowed, and
/// how a chosen level is delivered in the wire request.
/// </summary>
/// <param name="Supported">Whether the model supports reasoning effort.</param>
/// <param name="Levels">Ordered provider-native level names (lowest to highest).</param>
/// <param name="SupportsAuto">Whether "auto" (no explicit level) is allowed.</param>
/// <param name="Delivery">How a chosen level maps onto the request.</param>
public sealed record ReasoningCapability(
    bool Supported,
    IReadOnlyList<string> Levels,
    bool SupportsAuto,
    ReasoningDelivery Delivery)
{
    /// <summary>A capability instance representing a model that does not support reasoning effort.</summary>
    public static ReasoningCapability Unsupported { get; } =
        new(Supported: false, Levels: [], SupportsAuto: false, Delivery: ReasoningDelivery.None);
}
