using System.Text.Json.Serialization;

namespace Coda.Agent.Teams;

public sealed record ShutdownApproved(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("timestamp")] string Timestamp);
