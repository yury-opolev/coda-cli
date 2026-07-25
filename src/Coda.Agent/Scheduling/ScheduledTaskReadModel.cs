namespace Coda.Agent.Scheduling;

/// <summary>
/// A single scheduled definition projected into a display-ready read model. Produced by
/// <see cref="ScheduleControlService.List"/> and <see cref="ScheduleControlService.Create"/>;
/// consumed by the schedule tools, the serve RPCs, and (future) the TUI browser — all via the
/// same <see cref="ScheduleControlService"/> so the projection never drifts between surfaces.
/// </summary>
/// <param name="Id">The definition's persisted short identifier.</param>
/// <param name="Name">Optional human-readable label.</param>
/// <param name="Kind">Which selector produced this definition.</param>
/// <param name="Prompt">The prompt executed on each firing.</param>
/// <param name="Rule">Human-readable rule description (<c>ScheduleDisplay.DescribeRule</c>).</param>
/// <param name="TimeZone">The timezone the definition is interpreted in.</param>
/// <param name="NextRunUtc">Next scheduled execution time (UTC).</param>
/// <param name="NextRunLocal">Next run formatted as a local wall-clock string in the definition's zone.</param>
/// <param name="NextRunLocalLabel">The zone label that accompanies <paramref name="NextRunLocal"/>.</param>
/// <param name="State">Live runtime execution status.</param>
/// <param name="ActiveTaskId">The running/pending task id, when one exists.</param>
/// <param name="LastOutcome">Optional last terminal outcome metadata.</param>
public sealed record ScheduledTaskReadModel(
    string Id,
    string? Name,
    ScheduleKind Kind,
    string Prompt,
    string Rule,
    string TimeZone,
    DateTimeOffset NextRunUtc,
    string NextRunLocal,
    string NextRunLocalLabel,
    ScheduleRuntimeStatus State,
    string? ActiveTaskId,
    ScheduleTerminalMetadata? LastOutcome);
