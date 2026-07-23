using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record TurnCompleteEvent(
    [property: JsonPropertyName("stopReason")] string? StopReason,
    [property: JsonPropertyName("interrupted")] bool Interrupted)
{
    [JsonPropertyName("rootTurnId")]
    public string? RootTurnId { get; init; }

    [JsonPropertyName("activityId")]
    public string? ActivityId { get; init; }
}
