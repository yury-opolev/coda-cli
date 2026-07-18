namespace Coda.Agent.BackgroundTasks;

/// <summary>An immutable, UI-facing view of a background task's identity and lifecycle state.</summary>
public sealed record BackgroundTaskSnapshot(string Id, BackgroundTaskStatus Status);
