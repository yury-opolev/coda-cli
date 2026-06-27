using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record HistoryResult(
    [property: JsonPropertyName("messages")] IReadOnlyList<WireMessage> Messages);
