using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>Params for <c>session/setGoal</c>. Null or empty <c>goal</c> clears the current goal.</summary>
public sealed record SetGoalParams(
    [property: JsonPropertyName("goal")] string? Goal = null,
    [property: JsonPropertyName("maxDuration")] string? MaxDuration = null,
    [property: JsonPropertyName("maxContinuations")] int? MaxContinuations = null);
