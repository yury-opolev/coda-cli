using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>An image attached to a prompt: base64-encoded bytes + its MIME type (e.g. image/png).</summary>
public sealed record WireImage(
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("base64")] string Base64);
