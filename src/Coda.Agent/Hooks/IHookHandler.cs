namespace Coda.Agent.Hooks;

/// <summary>
/// Handles a single hook invocation for a specific non-command handler type
/// (<c>http</c>, <c>prompt</c>, or <c>agent</c>), returning structured
/// <see cref="HookOutput"/> directly without exit-code semantics.
/// </summary>
/// <remarks>
/// Timeouts are applied by the caller (<see cref="HookBus"/>) via the
/// <see cref="CancellationToken"/>. Implementations must propagate
/// <see cref="OperationCanceledException"/> so the bus can distinguish a
/// hook-scoped timeout from caller-level cancellation.
/// Any other exception is treated as an execution failure by the bus and
/// subjected to the hook's <c>failOpen</c> policy — implementations should
/// not swallow exceptions.
/// </remarks>
public interface IHookHandler
{
    /// <summary>
    /// Executes the hook for the given <paramref name="hook"/> configuration
    /// and <paramref name="payload"/> JSON string.
    /// </summary>
    Task<HookOutput> HandleAsync(UserHook hook, string payload, CancellationToken ct);
}
