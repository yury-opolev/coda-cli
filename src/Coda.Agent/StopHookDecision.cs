namespace Coda.Agent;

/// <summary>
/// One stop hook's verdict when the agent is about to finish. <see cref="Block"/>
/// = true means "don't stop yet"; <see cref="Reason"/> (always non-empty when
/// blocking) is injected back into the conversation to tell the agent what to do
/// next.
/// </summary>
public sealed record StopHookDecision
{
    private StopHookDecision(bool block, string? reason)
    {
        this.Block = block;
        this.Reason = reason;
    }

    public bool Block { get; }

    public string? Reason { get; }

    /// <summary>Let the agent stop.</summary>
    public static StopHookDecision Proceed { get; } = new(false, null);

    /// <summary>Keep the agent going, injecting <paramref name="reason"/>.</summary>
    public static StopHookDecision BlockWith(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A blocking stop hook must provide a non-empty reason.", nameof(reason));
        }

        return new StopHookDecision(true, reason);
    }
}
