using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record ToolResultEvent(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("isError")] bool IsError)
{
    [JsonPropertyName("rootTurnId")]
    public string? RootTurnId { get; init; }

    [JsonPropertyName("activityId")]
    public string? ActivityId { get; init; }

    [JsonPropertyName("callId")]
    public string? CallId { get; init; }

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}
