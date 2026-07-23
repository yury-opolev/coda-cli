using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record ToolCallEvent(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("inputJson")] string InputJson)
{
    [JsonPropertyName("rootTurnId")]
    public string? RootTurnId { get; init; }

    [JsonPropertyName("activityId")]
    public string? ActivityId { get; init; }

    [JsonPropertyName("callId")]
    public string? CallId { get; init; }

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; init; }
}
