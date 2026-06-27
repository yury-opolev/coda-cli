namespace Coda.Agent;

/// <summary>
/// The "step in when needed" lever. Evaluated when the agent would end its turn.
/// <paramref name="stopHookActive"/> is true when this stop is itself the result
/// of a previous block-and-continue, so a hook can avoid looping forever.
/// </summary>
public interface IStopHook
{
    Task<StopHookDecision> EvaluateAsync(ReplHookContext context, bool stopHookActive, CancellationToken cancellationToken = default);
}
