namespace Coda.Agent;

/// <summary>
/// Thrown by <see cref="SubagentHost"/> when a <c>SubagentStart</c> hook (fail-closed)
/// blocks the subagent from running. Propagated through <see cref="Tasks.TaskManager"/>
/// and caught by <see cref="Tools.TaskTool"/> to produce an error <see cref="ToolResult"/>.
/// </summary>
public sealed class SubagentStartBlockedException : Exception
{
    /// <summary>Initialises the exception with the hook's block reason.</summary>
    public SubagentStartBlockedException(string reason)
        : base(reason)
    {
    }
}
