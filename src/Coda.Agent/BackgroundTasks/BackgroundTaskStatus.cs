namespace Coda.Agent.BackgroundTasks;

/// <summary>The lifecycle state of a background task.</summary>
public enum BackgroundTaskStatus
{
    Running,
    Completed,
    Failed,
    Stopped,
}
