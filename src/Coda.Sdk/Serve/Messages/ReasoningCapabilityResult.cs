using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// Result of <c>model/reasoningCapability</c>: the reasoning capability for a
/// given (provider, model) pair.
/// </summary>
public sealed record ReasoningCapabilityResult(
    [property: JsonPropertyName("supported")] bool Supported,
    [property: JsonPropertyName("levels")] IReadOnlyList<string> Levels,
    [property: JsonPropertyName("supportsAuto")] bool SupportsAuto);
