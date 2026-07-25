namespace LlmClient;

/// <summary>
/// A model advertised by a provider's model-listing endpoint. <see cref="Id"/> is
/// the wire model id used in requests; <see cref="DisplayName"/> is an optional
/// human-friendly label (e.g. "Claude Sonnet 4.6") when the provider supplies one;
/// <see cref="ContextLimit"/> is the context-window size in tokens when the provider
/// reports it (e.g. Copilot's <c>/models</c> limits), else null;
/// <see cref="SupportedEndpoints"/> contains provider-specific request paths when
/// advertised; and <see cref="ReasoningLevels"/> contains the reasoning effort
/// levels supported by the model (e.g. from
/// <c>capabilities.supports.reasoning_effort</c> for Copilot models), else null.
/// </summary>
public sealed record ModelInfo(
    string Id,
    string? DisplayName = null,
    int? ContextLimit = null,
    IReadOnlyList<string>? SupportedEndpoints = null,
    IReadOnlyList<string>? ReasoningLevels = null);
