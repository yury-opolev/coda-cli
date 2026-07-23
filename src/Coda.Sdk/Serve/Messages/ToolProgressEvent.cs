using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// <c>event/toolProgress</c> payload — a liveness pulse emitted while a tool is still
/// executing, so the orchestrator can tell "a long tool is running" from "the process is
/// wedged" (the tool-execution counterpart to <see cref="StreamProgressEvent"/>). The
/// Bridge stamps its own receipt time (it drives the idle watchdog), so no timestamp is
/// carried. <paramref name="ElapsedMs"/> is how long the tool has been running so far.
/// </summary>
public sealed record ToolProgressEvent(string ToolName, long ElapsedMs)
{
    [JsonPropertyName("rootTurnId")]
    public string? RootTurnId { get; init; }

    [JsonPropertyName("activityId")]
    public string? ActivityId { get; init; }

    [JsonPropertyName("callId")]
    public string? CallId { get; init; }

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; init; }
}
