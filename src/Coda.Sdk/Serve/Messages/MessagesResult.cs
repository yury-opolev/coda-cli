using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record MessagesResult(
    [property: JsonPropertyName("messages")] IReadOnlyList<WireMessage> Messages,
    [property: JsonPropertyName("nextIndex")] int NextIndex);
