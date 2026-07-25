namespace Coda.Agent.Scheduling;

/// <summary>
/// The list/create/delete control surface for scheduled tasks. Implemented by
/// <see cref="ScheduleControlService"/>; extracted as an interface so the TUI browser,
/// slash command, and tests can depend on the abstraction rather than the concrete class.
/// </summary>
public interface IScheduleControl
{
    /// <summary>
    /// Returns all definitions projected into <see cref="ScheduledTaskReadModel"/> instances joined
    /// with live runtime state. Returns an empty list when no store is available (e.g. subagent context).
    /// </summary>
    IReadOnlyList<ScheduledTaskReadModel> List();

    /// <summary>
    /// Validates and persists <paramref name="request"/>. Returns a success carrying the read model on
    /// success, or a failure carrying the parser's exact error message on invalid input.
    /// </summary>
    ScheduleCreateResult Create(ScheduleCreateRequest request);

    /// <summary>
    /// Removes the definition with <paramref name="id"/>. Returns <see langword="true"/> when found
    /// and removed, <see langword="false"/> when not found or no store is available.
    /// </summary>
    bool Delete(string id);
}
