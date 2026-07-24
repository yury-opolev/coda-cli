namespace Coda.Agent;

/// <summary>
/// Runs a nested subagent (its own <see cref="AgentLoop"/> with a restricted tool set) to
/// completion and returns its final text. Implemented by <see cref="SubagentHost"/>. The task
/// manager owns registration/lifecycle and calls this to execute the child loop; the host wires
/// the child's task id, depth, and steering into the child <see cref="ToolContext"/>.
/// </summary>
public interface ISubagentHost
{
    Task<string> RunSubagentAsync(
        string subagentType,
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a nested subagent with the parent turn's tool-activity correlation context. Existing
    /// hosts need only implement the original overload; this compatibility bridge preserves that
    /// contract while hosts that understand activity identity can override the enriched overload.
    /// </summary>
    Task<string> RunSubagentAsync(
        string subagentType,
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        ToolActivityContext? parentActivity,
        CancellationToken cancellationToken = default) =>
        RunSubagentAsync(subagentType, prompt, sink, steering, taskId, depth, cancellationToken);
}
