using Coda.Agent.Goals;
using LlmClient;

namespace Coda.Agent;

/// <summary>
/// The agentic tool-use cycle as consumed by a session: drives an assistant turn to
/// completion over a conversation history. Extracted as a seam so callers can run a turn
/// against a fake loop in tests. <see cref="AgentLoop"/> is the production implementation.
/// </summary>
public interface IAgentLoop
{
    /// <summary>
    /// The goal status recorded by the most recent <see cref="RunAsync"/> call, or
    /// <c>null</c> when no goal was active.
    /// </summary>
    GoalStatus? LastGoalStatus { get; }

    /// <summary>
    /// Run the agentic loop over <paramref name="history"/>, emitting events to
    /// <paramref name="sink"/> until the model stops requesting tools or a bound is hit.
    /// </summary>
    /// <param name="history">The conversation history; the loop reads and appends to it in place.</param>
    /// <param name="sink">Receives streamed assistant text, tool calls, and results.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default);
}
