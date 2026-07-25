using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>A chunk of model reasoning text emitted during the current thinking burst.</summary>
public sealed record ThinkingEvent(
    [property: JsonPropertyName("delta")] string Delta);
