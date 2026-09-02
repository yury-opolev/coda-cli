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
/// <remarks>
/// <see cref="Model"/> and <see cref="ProviderId"/> say which entry is actually
/// in use and under which provider. The list alone does not, so a client
/// showing "the current model" can only guess — and the obvious guess, the
/// first entry, is whatever order the provider returned. That makes the status
/// bar name a model the engine is not running, and makes a model switch look as
/// though it were never saved.
/// </remarks>
public sealed record ModelsResult(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("models")] IReadOnlyList<WireModel> Models,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("providerId")] string? ProviderId = null);
