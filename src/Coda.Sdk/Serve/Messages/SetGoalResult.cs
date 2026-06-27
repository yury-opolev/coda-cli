using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>Result of <c>session/setGoal</c>: the current goal state after mutation.</summary>
public sealed record SetGoalResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("goal")] string? Goal,
    [property: JsonPropertyName("maxDuration")] string? MaxDuration,
    [property: JsonPropertyName("maxContinuations")] int? MaxContinuations);
