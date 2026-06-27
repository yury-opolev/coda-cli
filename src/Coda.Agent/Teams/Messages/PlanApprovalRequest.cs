using System.Text.Json.Serialization;

namespace Coda.Agent.Teams;

public sealed record PlanApprovalRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("planFilePath")] string PlanFilePath,
    [property: JsonPropertyName("planContent")] string PlanContent,
    [property: JsonPropertyName("requestId")] string RequestId);
