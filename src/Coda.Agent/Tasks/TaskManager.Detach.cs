namespace Coda.Agent.Tasks;

public sealed partial class TaskManager
{
    /// <summary>
    /// Requests that a running foreground shell task be promoted to the background (shells only).
    /// Returns Rejected for subagents (which use <c>task</c>/<c>task_start</c> instead), NotFound
    /// for unknown ids, and InvalidState when the task is not running.
    /// </summary>
    public TaskActionResult TryDetach(string id)
    {
        var t = Find(id);
        if (t is null) return TaskActionResult.NotFound;
        if (t.Kind != TaskKind.Shell) return TaskActionResult.Rejected;
        if (t.Status != TaskRunStatus.Running) return TaskActionResult.InvalidState;
        return t.TryRequestDetach() ? TaskActionResult.Ok : TaskActionResult.InvalidState;
    }
}
