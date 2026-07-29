using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// A <c>PostCompact</c> hook injected additional context into history after compaction.
/// This fires before skill re-attachment; together they represent the total post-compaction
/// context injection (PostCompact context first, then skill bodies).
/// </summary>
public sealed record PostCompactContextInjectedEvent(
    [property: JsonPropertyName("additionalContext")] string AdditionalContext);
