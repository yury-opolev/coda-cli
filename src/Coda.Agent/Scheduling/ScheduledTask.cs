namespace Coda.Agent.Scheduling;

/// <summary>The way a scheduled definition computes its due times.</summary>
public enum ScheduleKind
{
    /// <summary>Fixed recurring interval measured from schedule boundaries.</summary>
    Interval,

    /// <summary>One-shot execution at a specific UTC instant.</summary>
    At,

    /// <summary>Recurring five-field cron rule evaluated in a stored timezone.</summary>
    Cron,
}

/// <summary>Terminal result of a scheduled execution.</summary>
public enum ScheduleTerminalOutcome
{
    /// <summary>The execution completed successfully.</summary>
    Succeeded,

    /// <summary>The execution ended in an error.</summary>
    Failed,

    /// <summary>The execution was cancelled or stopped before completing.</summary>
    Stopped,
}

/// <summary>Last-known terminal outcome metadata for a scheduled definition.</summary>
/// <param name="Outcome">The terminal outcome classification.</param>
/// <param name="CompletedAtUtc">When the execution reached its terminal state (UTC).</param>
/// <param name="Summary">Optional short human-readable summary.</param>
public sealed record ScheduleTerminalMetadata(
    ScheduleTerminalOutcome Outcome,
    DateTimeOffset CompletedAtUtc,
    string? Summary);

/// <summary>
/// A persisted scheduled definition. Represents interval, one-shot, and cron schedules
/// with an explicit versioned schema.
/// </summary>
/// <param name="SchemaVersion">Persisted schema version for this record.</param>
/// <param name="Id">Short unique identifier.</param>
/// <param name="Name">Optional human-readable label.</param>
/// <param name="Kind">Which selector produced this definition.</param>
/// <param name="Prompt">The prompt to run when the definition fires.</param>
/// <param name="Interval">Recurring interval, when <see cref="Kind"/> is <see cref="ScheduleKind.Interval"/>.</param>
/// <param name="AtUtc">One-shot UTC instant, when <see cref="Kind"/> is <see cref="ScheduleKind.At"/>.</param>
/// <param name="Cron">Normalized cron expression, when <see cref="Kind"/> is <see cref="ScheduleKind.Cron"/>.</param>
/// <param name="TimeZoneId">Timezone the definition is interpreted in.</param>
/// <param name="NextRunUtc">Next scheduled execution time (UTC).</param>
/// <param name="CreatedAtUtc">When the definition was created (UTC).</param>
/// <param name="UpdatedAtUtc">When the definition was last mutated (UTC).</param>
/// <param name="LastTerminalOutcome">Optional last terminal outcome metadata.</param>
public sealed record ScheduledTask(
    int SchemaVersion,
    string Id,
    string? Name,
    ScheduleKind Kind,
    string Prompt,
    TimeSpan? Interval,
    DateTimeOffset? AtUtc,
    string? Cron,
    string TimeZoneId,
    DateTimeOffset NextRunUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ScheduleTerminalMetadata? LastTerminalOutcome)
{
    /// <summary>The current persisted schema version.</summary>
    public const int CurrentSchemaVersion = 2;
}
