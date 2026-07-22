using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>Ordered pending steering messages recalled from a running session.</summary>
public sealed record RecallSteeringResult(
    [property: JsonPropertyName("messages")] IReadOnlyList<RecalledSteeringMessage> Messages);
