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

    /// <summary>
    /// Runs a nested subagent with the parent turn's tool restriction applied monotonically to the
    /// child: the child can only be <em>at least as restricted</em> as the parent. A non-null
    /// <paramref name="parentToolRestriction"/> carries only the tool filter (AllowedTools or
    /// DeniedTools); system prompt, model, and effort are per-parent-turn overrides that must NOT
    /// bleed into a child that has its own system prompt and context.
    /// </summary>
    Task<string> RunSubagentAsync(
        string subagentType,
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        ToolActivityContext? parentActivity,
        TurnShape? parentToolRestriction,
        CancellationToken cancellationToken = default) =>
        RunSubagentAsync(subagentType, prompt, sink, steering, taskId, depth, parentActivity, cancellationToken);

    /// <summary>
    /// Runs a nested subagent from a single request object. This is the overload the task manager
    /// calls, and the only one that carries the caller's system-prompt influence
    /// (<see cref="SubagentRequest.SystemPrompt"/>).
    /// </summary>
    /// <remarks>
    /// The default implementation forwards to the positional overload and therefore drops the
    /// system-prompt influence. That is deliberate: a third-party host that predates this overload
    /// never supported caller-supplied prompt text, and silently ignoring it is safer than having
    /// the interface pretend it was applied.
    /// </remarks>
    Task<string> RunSubagentAsync(
        SubagentRequest request,
        IAgentSink sink,
        SteeringInbox steering,
        CancellationToken cancellationToken = default) =>
        RunSubagentAsync(
            request.SubagentType,
            request.Prompt,
            sink,
            steering,
            request.TaskId,
            request.Depth,
            request.ParentActivity,
            request.ParentToolRestriction,
            cancellationToken);
}
