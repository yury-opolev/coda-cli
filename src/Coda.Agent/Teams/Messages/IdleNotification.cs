using System.Text.Json.Serialization;

namespace Coda.Agent.Teams;

public sealed record IdleNotification(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("idleReason")] string? IdleReason,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("completedTaskId")] string? CompletedTaskId,
    [property: JsonPropertyName("completedStatus")] string? CompletedStatus,
    [property: JsonPropertyName("failureReason")] string? FailureReason);
