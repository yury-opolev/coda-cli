namespace Coda.Agent;

/// <summary>
/// Holds the registered hooks and executes the two buses: post-sampling hooks
/// run in the background after each assistant turn (the caller drains them), and
/// stop hooks run synchronously at the stop point and are aggregated into a
/// single <see cref="StopHookOutcome"/>.
/// </summary>
public sealed class AgentHooks
{
    private readonly IReadOnlyList<IPostSamplingHook> postSampling;
    private readonly IReadOnlyList<IStopHook> stop;

    public AgentHooks(
        IReadOnlyList<IPostSamplingHook>? postSampling = null,
        IReadOnlyList<IStopHook>? stop = null)
    {
        this.postSampling = postSampling ?? [];
        this.stop = stop ?? [];
    }

    public bool HasStopHooks => this.stop.Count > 0;

    /// <summary>
    /// Start every post-sampling hook on a background task and return the started
    /// tasks so the caller can drain them. Hook exceptions are swallowed (observe
    /// hooks are best-effort and own their side effects).
    /// </summary>
    public IReadOnlyList<Task> FirePostSampling(ReplHookContext context, CancellationToken cancellationToken)
    {
        if (this.postSampling.Count == 0)
        {
            return [];
        }

        var tasks = new List<Task>(this.postSampling.Count);
        foreach (var hook in this.postSampling)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await hook.RunAsync(context, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // shutting down — ignore
                }
                catch
                {
                    // best-effort observer; never disrupt the main run
                }
            }, cancellationToken));
        }

        return tasks;
    }

    /// <summary>
    /// Run every stop hook in order and combine their verdicts. If any blocks, the
    /// agent continues with the blocking reasons joined into one injected message.
    /// </summary>
    public async Task<StopHookOutcome> RunStopHooksAsync(
        ReplHookContext context,
        bool stopHookActive,
        CancellationToken cancellationToken)
    {
        var shouldBlock = false;
        var reasons = new List<string>();
        foreach (var hook in this.stop)
        {
            var decision = await hook.EvaluateAsync(context, stopHookActive, cancellationToken).ConfigureAwait(false);
            if (decision.Block)
            {
                shouldBlock = true;
                if (!string.IsNullOrWhiteSpace(decision.Reason))
                {
                    reasons.Add(decision.Reason);
                }
            }
        }

        if (!shouldBlock)
        {
            return StopHookOutcome.Stop;
        }

        return new StopHookOutcome(true, string.Join("\n\n", reasons));
    }
}
