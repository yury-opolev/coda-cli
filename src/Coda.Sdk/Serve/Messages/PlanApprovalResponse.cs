using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record PlanApprovalResponse(
    [property: JsonPropertyName("approve")] bool Approve);
