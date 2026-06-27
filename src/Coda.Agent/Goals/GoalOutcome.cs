namespace Coda.Agent.Goals;

/// <summary>The terminal status of an autonomous goal run.</summary>
public enum GoalOutcome
{
    /// <summary>No goal was active for this run.</summary>
    None,

    /// <summary>The judge verified the goal as fully complete.</summary>
    Met,

    /// <summary>The budget (time or turns, incl. the one extension) was exhausted before completion.</summary>
    Unmet,
}
