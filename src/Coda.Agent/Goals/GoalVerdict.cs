namespace Coda.Agent.Goals;

/// <summary>The supervisor's decision at a stop point.</summary>
public abstract record GoalVerdict
{
    private GoalVerdict()
    {
    }

    /// <summary>The goal is not yet met; inject <see cref="Nudge"/> and keep working.</summary>
    public sealed record Continue(string Nudge) : GoalVerdict;

    /// <summary>The run should end now. <see cref="Met"/> distinguishes success from budget exhaustion.</summary>
    public sealed record Stop(bool Met) : GoalVerdict;

    /// <summary>Budget exhausted with goal unmet: ask the operator <see cref="Question"/>, then extend-or-stop.</summary>
    public sealed record Escalate(string Question, string? Remaining) : GoalVerdict;
}
