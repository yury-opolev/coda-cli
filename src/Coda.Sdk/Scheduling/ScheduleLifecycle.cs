namespace Coda.Sdk.Scheduling;

/// <summary>The lifecycle transition a <see cref="ScheduleLifecycleEvent"/> reports.</summary>
public enum ScheduleLifecycleKind
{
    /// <summary>A scheduled execution has been registered and started.</summary>
    Started,

    /// <summary>A scheduled execution completed successfully.</summary>
    Completed,

    /// <summary>A scheduled execution failed, or could not be launched.</summary>
    Failed,

    /// <summary>A scheduled execution was cancelled or stopped before completing.</summary>
    Stopped,
}

/// <summary>
/// Host-neutral notification that a scheduled definition changed execution state. Adapters render
/// these as TUI notices or serve JSON-RPC events without the runtime referencing either host.
/// </summary>
/// <param name="DefinitionId">The scheduled definition's persisted id.</param>
/// <param name="DefinitionName">Optional human-readable definition label.</param>
/// <param name="TaskId">The managed task id, when one exists (null for pre-launch failures).</param>
/// <param name="Kind">The lifecycle transition.</param>
/// <param name="Timestamp">When the transition occurred (UTC).</param>
/// <param name="Summary">Optional short human-readable detail (result text or error).</param>
public sealed record ScheduleLifecycleEvent(
    string DefinitionId,
    string? DefinitionName,
    string? TaskId,
    ScheduleLifecycleKind Kind,
    DateTimeOffset Timestamp,
    string? Summary);

/// <summary>
/// Sink that receives <see cref="ScheduleLifecycleEvent"/> notifications from the schedule runtime.
/// Implementations must be resilient: the runtime isolates and swallows sink faults so a bad sink
/// cannot stall or stop scheduling.
/// </summary>
public interface IScheduleLifecycleSink
{
    /// <summary>Publishes a lifecycle <paramref name="value"/>.</summary>
    ValueTask PublishAsync(ScheduleLifecycleEvent value, CancellationToken cancellationToken = default);
}

/// <summary>A no-op <see cref="IScheduleLifecycleSink"/> for hosts that do not surface events.</summary>
public sealed class NullScheduleLifecycleSink : IScheduleLifecycleSink
{
    /// <summary>The shared singleton instance.</summary>
    public static NullScheduleLifecycleSink Instance { get; } = new();

    private NullScheduleLifecycleSink()
    {
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(ScheduleLifecycleEvent value, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
