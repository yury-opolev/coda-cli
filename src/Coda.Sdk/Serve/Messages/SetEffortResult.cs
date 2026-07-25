using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>Result of <c>session/setEffort</c>.</summary>
public sealed record SetEffortResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("applied")] string? Applied,
    [property: JsonPropertyName("note")] string? Note = null);
