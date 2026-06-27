using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record AssistantTextEvent(
    [property: JsonPropertyName("delta")] string Delta);
