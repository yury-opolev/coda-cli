using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record UsageEvent(
    [property: JsonPropertyName("inputTokens")] int InputTokens,
    [property: JsonPropertyName("outputTokens")] int OutputTokens);
