namespace Coda.Agent;

/// <summary>
/// Runs a nested subagent (its own <see cref="AgentLoop"/> with a restricted tool
/// set) to completion and returns its final text. Implemented by
/// <see cref="SubagentHost"/>; surfaced to the <c>task</c> tool via
/// <see cref="ToolContext.Subagents"/>.
/// </summary>
public interface ISubagentHost
{
    Task<string> RunSubagentAsync(
        string subagentType,
        string prompt,
        IAgentSink parentSink,
        CancellationToken cancellationToken = default);
}
