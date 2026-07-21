namespace Coda.Agent.Tasks;

/// <summary>Outcome of an awaited task-completion request.</summary>
public enum TaskWaitOutcome
{
    /// <summary>The id is unknown OR the caller is unauthorized (indistinguishable).</summary>
    NotFound,

    /// <summary>The task is terminal (completed/failed/stopped).</summary>
    Terminal,
}

public sealed partial class TaskManager
{
    /// <summary>
    /// Awaits until an authorized task reaches a terminal state, honoring <paramref name="cancellationToken"/>.
    /// Authorization is checked BEFORE any waiting so timing can never reveal a task outside the caller's
    /// subtree: an unknown id and an unauthorized target both return <see cref="TaskWaitOutcome.NotFound"/>
    /// immediately, without waiting. Timing out is the caller's concern (via its own token); this method
    /// never stops the task.
    /// </summary>
    public async Task<TaskWaitOutcome> WaitForTerminalAsync(
        string id, string? callerTaskId, CancellationToken cancellationToken = default)
    {
        var t = Find(id);
        if (t is null || !IsAuthorizedCaller(id, callerTaskId))
        {
            return TaskWaitOutcome.NotFound;
        }

        if (t.Status != TaskRunStatus.Running)
        {
            return TaskWaitOutcome.Terminal;
        }

        // Completion never faults and completes exactly once at the terminal transition (or disposal).
        await t.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        return TaskWaitOutcome.Terminal;
    }

    /// <summary>Compatibility overload for the main agent (full authority over every task).</summary>
    public Task<TaskWaitOutcome> WaitForTerminalAsync(string id, CancellationToken cancellationToken = default) =>
        WaitForTerminalAsync(id, callerTaskId: null, cancellationToken);

    /// <summary>
    /// Caller-scoped <see cref="Remove(string)"/>: removes an authorized terminal task while preserving its
    /// persistent log. Returns <see cref="TaskActionResult.Denied"/> for an unauthorized target, checked
    /// before any state inspection so it is indistinguishable from <see cref="TaskActionResult.NotFound"/>.
    /// </summary>
    public TaskActionResult Remove(string id, string? callerTaskId)
    {
        if (Find(id) is null) return TaskActionResult.NotFound;
        if (!IsAuthorizedCaller(id, callerTaskId)) return TaskActionResult.Denied;
        return Remove(id);
    }

    /// <summary>
    /// Caller-scoped <see cref="TryDetach(string)"/>: promotes an authorized running foreground shell to the
    /// background. Returns <see cref="TaskActionResult.Denied"/> for an unauthorized target, checked before
    /// kind/state inspection so it is indistinguishable from <see cref="TaskActionResult.NotFound"/>.
    /// </summary>
    public TaskActionResult TryDetach(string id, string? callerTaskId)
    {
        if (Find(id) is null) return TaskActionResult.NotFound;
        if (!IsAuthorizedCaller(id, callerTaskId)) return TaskActionResult.Denied;
        return TryDetach(id);
    }
}
