using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>Params for <c>session/setEffort</c>.</summary>
/// <param name="Effort">
/// The effort level to set (<c>"low"</c>, <c>"medium"</c>, <c>"high"</c>, <c>"max"</c>),
/// <c>"auto"</c> to clear, or <c>null</c> to clear.
/// </param>
public sealed record SetEffortParams(
    [property: JsonPropertyName("effort")] string? Effort = null);
