using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record InterruptResult(
    [property: JsonPropertyName("ok")] bool Ok);
