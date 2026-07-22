using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>Notifies an orchestrator that queued steering messages entered provider history.</summary>
public sealed record SteeringDeliveredEvent(
    [property: JsonPropertyName("messageIds")] IReadOnlyList<string> MessageIds);
