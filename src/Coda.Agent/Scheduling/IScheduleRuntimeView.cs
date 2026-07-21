namespace Coda.Agent.Scheduling;

/// <summary>Live execution status of a scheduled definition, as projected to the model.</summary>
public enum ScheduleRuntimeStatus
{
    /// <summary>No execution is in progress and none is queued.</summary>
    Idle,

    /// <summary>An execution is currently running.</summary>
    Running,

    /// <summary>An execution is due and queued but has not started yet.</summary>
    Pending,
}

/// <summary>
/// Point-in-time runtime state for a single scheduled definition.
/// </summary>
/// <param name="Status">The live execution status.</param>
/// <param name="ActiveTaskId">The id of the running/pending task, when one exists.</param>
public sealed record ScheduleRuntimeState(
    ScheduleRuntimeStatus Status,
    string? ActiveTaskId);

/// <summary>
/// Immutable runtime-state entry for one definition, keyed by its persisted id.
/// </summary>
/// <param name="DefinitionId">The scheduled definition's id.</param>
/// <param name="Status">The live execution status.</param>
/// <param name="ActiveTaskId">The id of the running/pending task, when one exists.</param>
public sealed record ScheduleRuntimeSnapshot(
    string DefinitionId,
    ScheduleRuntimeStatus Status,
    string? ActiveTaskId);

/// <summary>
/// Host-neutral, read-only projection of the schedule runtime. Lets <c>schedule_list</c> report
/// idle/running/pending state and active task ids without depending on any concrete runtime.
/// </summary>
public interface IScheduleRuntimeView
{
    /// <summary>
    /// Attempts to read the runtime <paramref name="state"/> for the definition with the given
    /// <paramref name="scheduleId"/>. Returns <c>false</c> when the runtime has no entry for it,
    /// in which case callers should treat the definition as <see cref="ScheduleRuntimeStatus.Idle"/>.
    /// </summary>
    bool TryGetState(string scheduleId, out ScheduleRuntimeState state);

    /// <summary>Returns a fresh, immutable snapshot of every tracked definition's runtime state.</summary>
    IReadOnlyList<ScheduleRuntimeSnapshot> GetSnapshot();
}
