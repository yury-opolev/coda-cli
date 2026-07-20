namespace Coda.Agent.Tasks;

/// <summary>
/// Lifecycle state of a managed task. Named TaskRunStatus (not TaskStatus) to
/// avoid CS0104 ambiguity with System.Threading.Tasks.TaskStatus.
/// </summary>
public enum TaskRunStatus
{
    Running,
    Completed,
    Failed,
    Stopped,
}
