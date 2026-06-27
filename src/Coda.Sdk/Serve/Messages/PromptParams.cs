using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record PromptParams
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("images")]
    public IReadOnlyList<WireImage>? Images { get; init; }
}
