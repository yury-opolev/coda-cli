using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// A reasoning burst completed. <see cref="ElapsedMs"/> is the wall-clock duration of the burst.
/// <see cref="ThinkingTokens"/> is the normalized token count from the provider usage stream when
/// present, or <see langword="null"/> when not available for this provider or burst.
/// </summary>
public sealed record ThinkingCompleteEvent(
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs,
    [property: JsonPropertyName("thinkingTokens")] int? ThinkingTokens);
