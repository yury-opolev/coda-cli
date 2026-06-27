using System.Text.Json.Serialization;

namespace Coda.Agent.Teams;

public sealed record ShutdownRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("timestamp")] string Timestamp);
