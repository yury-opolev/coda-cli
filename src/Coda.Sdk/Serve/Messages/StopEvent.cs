using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record StopEvent(
    [property: JsonPropertyName("stopReason")] string? StopReason);
