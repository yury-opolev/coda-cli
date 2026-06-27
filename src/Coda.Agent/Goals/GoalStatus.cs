namespace Coda.Agent.Goals;

/// <summary>
/// A snapshot of an autonomous goal run, surfaced to callers (serve result, headless
/// output, TUI status). <see cref="GoalOutcome.None"/> means no goal was active.
/// </summary>
public sealed record GoalStatus(
    GoalOutcome Outcome,
    string? Remaining,
    int Continuations,
    TimeSpan Elapsed,
    bool Escalated,
    bool ExtensionUsed)
{
    /// <summary>The "no goal active" snapshot.</summary>
    public static GoalStatus None { get; } =
        new(GoalOutcome.None, null, 0, TimeSpan.Zero, false, false);

    /// <summary>True when the goal was not active or was verified complete (i.e. not Unmet).</summary>
    public bool IsSuccessful => this.Outcome != GoalOutcome.Unmet;
}
