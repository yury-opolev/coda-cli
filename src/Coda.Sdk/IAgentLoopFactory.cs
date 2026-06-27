using Coda.Agent;

namespace Coda.Sdk;

/// <summary>
/// Builds the per-turn <see cref="IAgentLoop"/> from an <see cref="AgentLoopSpec"/>. Injected
/// into <see cref="CodaSession"/> so a turn can be driven against a fake loop in tests. The
/// default implementation (<see cref="DefaultAgentLoopFactory"/>) constructs a real
/// <see cref="AgentLoop"/> from the spec verbatim.
/// </summary>
public interface IAgentLoopFactory
{
    /// <summary>Create the agent loop for the turn described by <paramref name="spec"/>.</summary>
    /// <param name="spec">The bundled construction arguments for the loop.</param>
    IAgentLoop Create(AgentLoopSpec spec);
}
