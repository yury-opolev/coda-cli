using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record PlanApprovalRequest(
    [property: JsonPropertyName("plan")] string Plan);
