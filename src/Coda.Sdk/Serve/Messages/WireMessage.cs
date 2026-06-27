using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record WireMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);
