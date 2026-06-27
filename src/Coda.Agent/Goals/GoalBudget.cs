namespace Coda.Agent.Goals;

/// <summary>
/// The bound on an autonomous goal run: a wall-clock duration and a turn (continuation)
/// count, whichever trips first. Supports exactly one bounded extension, granted when an
/// orchestrator answers the at-bound escalation. Not thread-safe; consulted from the
/// single agent loop.
/// </summary>
public sealed class GoalBudget
{
    private readonly Func<TimeSpan> elapsed;
    private readonly double extensionFraction;
    private TimeSpan maxDuration;
    private int maxContinuations;
    private int continuations;
    private bool extensionUsed;

    /// <param name="elapsed">Returns wall-clock time since the goal run began. Injectable for tests.</param>
    public GoalBudget(TimeSpan maxDuration, int maxContinuations, double extensionFraction, Func<TimeSpan> elapsed)
    {
        ArgumentNullException.ThrowIfNull(elapsed);
        this.maxDuration = maxDuration;
        this.maxContinuations = maxContinuations;
        this.extensionFraction = extensionFraction;
        this.elapsed = elapsed;
    }

    public int Continuations => this.continuations;

    public bool ExtensionUsed => this.extensionUsed;

    public TimeSpan Elapsed => this.elapsed();

    public bool IsExhausted =>
        this.elapsed() >= this.maxDuration || this.continuations >= this.maxContinuations;

    /// <summary>Record that the agent was nudged to continue once.</summary>
    public void RecordContinuation() => this.continuations++;

    /// <summary>
    /// Grant the single bounded extension, raising both ceilings by the extension
    /// fraction. Returns false if an extension was already granted.
    /// </summary>
    public bool GrantExtension()
    {
        if (this.extensionUsed)
        {
            return false;
        }

        this.extensionUsed = true;
        // Always raise both ceilings by at least one unit so the extension actually
        // unblocks the run, even for small/zero budgets where the fractional bump rounds
        // to nothing. Otherwise the operator's guidance would be discarded immediately.
        var durationBump = this.maxDuration * this.extensionFraction;
        this.maxDuration += durationBump > TimeSpan.Zero ? durationBump : TimeSpan.FromTicks(1);
        this.maxContinuations += Math.Max(1, (int)Math.Ceiling(this.maxContinuations * this.extensionFraction));
        return true;
    }

    /// <summary>Wall-clock budget factory using a real stopwatch started now.</summary>
    public static GoalBudget StartNow(TimeSpan maxDuration, int maxContinuations, double extensionFraction)
    {
        var start = System.Diagnostics.Stopwatch.StartNew();
        return new GoalBudget(maxDuration, maxContinuations, extensionFraction, () => start.Elapsed);
    }
}
