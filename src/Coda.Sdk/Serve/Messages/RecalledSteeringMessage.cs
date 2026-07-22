using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>A still-pending steering message returned by <c>session/recallSteering</c>.</summary>
public sealed record RecalledSteeringMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("enqueuedAt")] DateTimeOffset EnqueuedAt);
