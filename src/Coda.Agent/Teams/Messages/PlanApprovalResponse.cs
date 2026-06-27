using System.Text.Json.Serialization;

namespace Coda.Agent.Teams;

public sealed record PlanApprovalResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("approved")] bool Approved,
    [property: JsonPropertyName("feedback")] string? Feedback,
    [property: JsonPropertyName("timestamp")] string Timestamp);
