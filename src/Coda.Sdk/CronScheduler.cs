using Coda.Agent.Scheduling;

namespace Coda.Sdk;

/// <summary>
/// Pure tick-logic for the scheduled-task system. Given a <see cref="ScheduledTaskStore"/>
/// and a clock, determines which tasks are due and handles post-fire bookkeeping.
///
/// NOTE: Wiring this to fire <c>CodaSession.RunAsync</c> inside a real long-running host
/// (e.g. a background <see cref="System.Threading.Timer"/> or a hosted service) is
/// intentionally deferred — only the testable core is implemented here.
/// </summary>
public sealed class CronScheduler
{
    private readonly ScheduledTaskStore store;
    private readonly Func<DateTime> nowUtcFactory;

    public CronScheduler(ScheduledTaskStore store, Func<DateTime> nowUtcFactory)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.nowUtcFactory = nowUtcFactory ?? throw new ArgumentNullException(nameof(nowUtcFactory));
    }

    /// <summary>
    /// Returns all tasks whose <see cref="ScheduledTask.NextRunUtc"/> is at or before
    /// <paramref name="nowUtc"/>. The caller is responsible for actually running them and
    /// then calling <see cref="MarkFired"/> for each.
    /// </summary>
    public IReadOnlyList<ScheduledTask> DueTasks(DateTime nowUtc)
    {
        return [.. this.store.Items.Where(t => t.NextRunUtc.UtcDateTime <= nowUtc)];
    }

    /// <summary>
    /// Records that <paramref name="task"/> has fired at <paramref name="nowUtc"/>.
    /// <list type="bullet">
    ///   <item>Recurring tasks: NextRunUtc is advanced to the next cron occurrence after <paramref name="nowUtc"/>.</item>
    ///   <item>One-shot tasks: the task is removed from the store.</item>
    /// </list>
    /// </summary>
    public void MarkFired(ScheduledTask task, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(task);

        // TEMPORARY Task-1 compatibility: the legacy scheduler treated Kind==Cron definitions as
        // recurring and everything else as one-shot. Task 6 replaces this with the definition-aware
        // ScheduleRuntime state machine.
        if (task.Kind != ScheduleKind.Cron)
        {
            this.store.Remove(task.Id);
            return;
        }

        if (!Coda.Agent.Scheduling.CronExpression.TryParse(task.Cron ?? string.Empty, out var cronExpr, out _)
            || cronExpr is null)
        {
            // If the expression somehow became invalid, remove the task to avoid a stuck entry.
            this.store.Remove(task.Id);
            return;
        }

        try
        {
            var nextRun = new DateTimeOffset(cronExpr.NextOccurrence(nowUtc));
            this.store.Replace(task with { NextRunUtc = nextRun });
        }
        catch (InvalidOperationException)
        {
            // Valid expression but no occurrence within the bound (e.g. "0 0 30 2 *") — drop it rather than throw into the host loop.
            this.store.Remove(task.Id);
        }
    }
}
