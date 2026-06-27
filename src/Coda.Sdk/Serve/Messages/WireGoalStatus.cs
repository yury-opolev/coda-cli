using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// Wire shape of a goal-run outcome, included in the <c>session/prompt</c> result when a goal
/// was active and did not produce <c>None</c>.
/// </summary>
public sealed record WireGoalStatus(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("remaining")] string? Remaining,
    [property: JsonPropertyName("continuations")] int Continuations,
    [property: JsonPropertyName("elapsedSeconds")] double ElapsedSeconds,
    [property: JsonPropertyName("escalated")] bool Escalated,
    [property: JsonPropertyName("extensionUsed")] bool ExtensionUsed);
