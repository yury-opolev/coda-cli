using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record TurnCompleteEvent(
    [property: JsonPropertyName("stopReason")] string? StopReason,
    [property: JsonPropertyName("interrupted")] bool Interrupted);
