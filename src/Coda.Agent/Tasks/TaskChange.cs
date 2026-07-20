namespace Coda.Agent.Tasks;

/// <summary>What a change notification is about.</summary>
public enum TaskChangeKind
{
    Created,
    Status,
    Output,
    Removed,
}

/// <summary>A bounded change notification: which task, its version at publish time, and the kind.</summary>
public sealed record TaskChange(string TaskId, long Version, TaskChangeKind Kind);
