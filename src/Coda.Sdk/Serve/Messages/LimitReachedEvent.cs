using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// A recoverable per-turn limit was hit and the turn ended early (not a crash). <see cref="Kind"/>
/// is a stable machine-readable reason (e.g. "max_tokens", "max_tool_iterations") the orchestrator
/// can branch on; <see cref="Message"/> is the human-readable explanation.
/// </summary>
public sealed record LimitReachedEvent(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("message")] string Message);
