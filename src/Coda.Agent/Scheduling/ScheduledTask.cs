namespace Coda.Agent.Scheduling;

/// <summary>A single scheduled task entry.</summary>
/// <param name="Id">Short unique identifier.</param>
/// <param name="Cron">The 5-field cron expression.</param>
/// <param name="Prompt">The prompt to run when the task fires.</param>
/// <param name="Recurring">
///   When <c>true</c> the task reschedules itself after firing;
///   when <c>false</c> it is removed after the first execution.
/// </param>
/// <param name="NextRunUtc">Next scheduled execution time (UTC).</param>
public sealed record ScheduledTask(
    string Id,
    string Cron,
    string Prompt,
    bool Recurring,
    DateTime NextRunUtc);
