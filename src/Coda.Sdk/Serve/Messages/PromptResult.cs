using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record PromptResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("stopReason")] string? StopReason,
    [property: JsonPropertyName("interrupted")] bool Interrupted)
{
    /// <summary>Goal-run outcome. Present when a goal was active and produced a non-None outcome.</summary>
    [JsonPropertyName("goalStatus")]
    public WireGoalStatus? GoalStatus { get; init; }

    /// <summary>
    /// Failure reason when the turn did not succeed (e.g. a provider error like an HTTP 400
    /// model_not_supported). Null on success and on interruption. Lets the orchestrator surface
    /// <em>why</em> a turn failed instead of seeing a bare <c>ok:false</c>.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
