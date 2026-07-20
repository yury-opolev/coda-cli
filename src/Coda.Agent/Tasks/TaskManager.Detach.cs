namespace Coda.Agent.Tasks;

public sealed partial class TaskManager
{
    /// <summary>
    /// Requests that a running foreground shell task be promoted to the background (shells only).
    /// On success it atomically flips the task's mode to <see cref="TaskExecutionMode.Background"/>,
    /// signals the detach so the shell runner hands the process to its background finalizer, bumps
    /// the version, and publishes a <see cref="TaskChangeKind.Mode"/> change carrying that exact
    /// version so subscribers/snapshots can observe the promotion.
    ///
    /// Returns <see cref="TaskActionResult.Rejected"/> for subagents (which use <c>task</c>/
    /// <c>task_start</c> instead), <see cref="TaskActionResult.NotFound"/> for unknown ids, and
    /// <see cref="TaskActionResult.InvalidState"/> (with no event published) when the task is not
    /// a running foreground task — i.e. it is already terminal, or already in the background
    /// (already detached). The no-op cases publish nothing and leave the version untouched.
    ///
    /// Detach-vs-cancel race: <see cref="TaskActionResult.Ok"/> means only that the mode
    /// transition was accepted. A concurrent cancellation may win the terminal transition
    /// immediately afterwards, so a terminal <see cref="TaskRunStatus"/> (and its Status change at
    /// the next version) can follow the accepted promotion. The two transitions serialize under
    /// the task lock, so versions stay monotonic and the Mode change always precedes any
    /// subsequent terminal Status change.
    /// </summary>
    public TaskActionResult TryDetach(string id)
    {
        var t = Find(id);
        if (t is null) return TaskActionResult.NotFound;
        if (t.Kind != TaskKind.Shell) return TaskActionResult.Rejected;

        // Atomically promote a still-running foreground task. This single gated step rejects both
        // the already-terminal and already-background (already-detached) cases with no event.
        if (!t.TryPromoteToBackground(out var version)) return TaskActionResult.InvalidState;

        // Signal the runner to hand the process to its background finalizer. The promotion above
        // is the source of truth for the mode; this only wakes the foreground await.
        t.TryRequestDetach();
        Publish(id, version, TaskChangeKind.Mode);
        return TaskActionResult.Ok;
    }
}
