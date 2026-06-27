using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>One model in a <see cref="ModelsResult"/>.</summary>
public sealed record WireModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("contextLimit")] int? ContextLimit);

/// <summary>
/// Result of <c>session/models</c>: the resolved model list plus its source
/// (<c>live</c> / <c>catalog</c> / <c>builtin</c>).
/// </summary>
public sealed record ModelsResult(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("models")] IReadOnlyList<WireModel> Models);
