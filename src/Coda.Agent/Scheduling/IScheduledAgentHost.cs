namespace Coda.Agent.Scheduling;

/// <summary>
/// Runs a scheduled root agent (its own <see cref="AgentLoop"/>) to completion and returns its
/// final text. Implemented in a later task by the concrete scheduled-agent host; the task manager
/// owns registration/lifecycle and calls this to execute the scheduled work, wiring the root's
/// task id, depth, steering inbox, and output sink through to the child <see cref="ToolContext"/>.
/// Mirrors <see cref="ISubagentHost"/> but has no <c>subagentType</c> — a scheduled root is
/// launched from a schedule definition rather than a parent agent's tool call.
/// </summary>
public interface IScheduledAgentHost
{
    Task<string> RunScheduledAsync(
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        CancellationToken cancellationToken);
}
