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
}
