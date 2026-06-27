using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record MessagesParams(
    [property: JsonPropertyName("sinceIndex")] int SinceIndex);
