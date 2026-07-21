using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// <c>event/scheduleLifecycle</c> payload — a typed notification that a session-owned scheduled
/// definition changed execution state, forwarded to the orchestrator so it can surface
/// scheduled-run progress alongside a live turn. <paramref name="State"/> is the deterministic
/// lower-case transition (<c>started</c>, <c>completed</c>, <c>failed</c>, or <c>stopped</c>)
/// mapped from the host-neutral <see cref="Coda.Sdk.Scheduling.ScheduleLifecycleKind"/>.
/// Optional fields are omitted from the wire when null.
/// </summary>
/// <remarks>
/// This is a distinct wire type from the shared <see cref="Coda.Sdk.Scheduling.ScheduleLifecycleEvent"/>
/// (which carries a typed enum, not a wire string). A positional record with explicit
/// <see cref="JsonPropertyName"/> attributes keeps the JSON shape stable and camelCase — never a
/// ValueTuple <c>Item1</c>/<c>Item2</c> projection.
/// </remarks>
/// <param name="DefinitionId">The scheduled definition's persisted id.</param>
/// <param name="DefinitionName">Optional human-readable definition label.</param>
/// <param name="TaskId">The managed task id, when one exists (null for pre-launch failures).</param>
/// <param name="State">The transition: <c>started</c> | <c>completed</c> | <c>failed</c> | <c>stopped</c>.</param>
/// <param name="Timestamp">When the transition occurred (UTC).</param>
/// <param name="Summary">Optional short human-readable detail (result text or error).</param>
public sealed record ScheduleLifecycleEvent(
    [property: JsonPropertyName("definitionId")] string DefinitionId,
    [property: JsonPropertyName("definitionName")] string? DefinitionName,
    [property: JsonPropertyName("taskId")] string? TaskId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("summary")] string? Summary);
