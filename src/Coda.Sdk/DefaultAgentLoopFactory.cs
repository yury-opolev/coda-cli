using Coda.Agent;

namespace Coda.Sdk;

/// <summary>
/// Default <see cref="IAgentLoopFactory"/>: constructs a real <see cref="AgentLoop"/> from the
/// spec, mapping each field to the corresponding constructor argument verbatim so the loop built
/// is identical to the pre-seam per-turn construction. Used whenever a <see cref="CodaSession"/>
/// is constructed without an explicit loop factory.
/// </summary>
public sealed class DefaultAgentLoopFactory : IAgentLoopFactory
{
    /// <inheritdoc />
    public IAgentLoop Create(AgentLoopSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new AgentLoop(
            spec.Client,
            spec.Tools,
            spec.Permissions,
            spec.Options,
            spec.Subagents,
            spec.Hooks,
            todos: spec.Todos,
            schedules: spec.Schedules,
            userQuestion: spec.UserQuestion,
            userHooks: spec.UserHooks,
            planApprover: spec.PlanApprover,
            tasks: spec.Tasks,
            lsp: spec.Lsp,
            lspDiagnostics: spec.LspDiagnostics,
            toolSearch: spec.ToolSearch,
            goal: spec.Goal,
            compactAsync: spec.CompactAsync,
            steering: spec.Steering,
            logger: spec.Logger,
            persistTurnAsync: spec.PersistTurnAsync,
            gate: spec.Gate);
    }
}
