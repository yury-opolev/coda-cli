namespace Coda.Agent.Tasks;

/// <summary>What a change notification is about.</summary>
public enum TaskChangeKind
{
    Created,
    Status,
    Output,

    /// <summary>The task's execution mode changed (e.g. a foreground shell promoted to the background).</summary>
    Mode,
    Removed,
}

/// <summary>A bounded change notification: which task, its version at publish time, and the kind.</summary>
public sealed record TaskChange(string TaskId, long Version, TaskChangeKind Kind);
