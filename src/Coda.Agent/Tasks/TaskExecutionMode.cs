namespace Coda.Agent.Tasks;

/// <summary>
/// Whether a managed task runs in the foreground (the caller awaits its result) or the
/// background (started fire-and-forget, polled via <c>task_output</c>). A foreground shell can
/// be promoted to the background at runtime via <see cref="TaskManager.TryDetach"/>; subagents
/// pick their mode at start (<c>task</c> vs <c>task_start</c>) and are never promoted.
/// </summary>
public enum TaskExecutionMode
{
    Foreground,
    Background,
}
