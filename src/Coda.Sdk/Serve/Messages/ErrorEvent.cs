using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record ErrorEvent(
    [property: JsonPropertyName("message")] string Message);
