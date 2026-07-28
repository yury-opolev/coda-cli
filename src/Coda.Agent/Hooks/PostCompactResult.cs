namespace Coda.Agent.Hooks;

/// <summary>
/// The merged outcome of all <c>PostCompact</c> hook invocations, representing
/// content the hook wants re-injected after the summary.
/// </summary>
public sealed record PostCompactResult
{
    /// <summary>
    /// Additional context to inject into history after compaction. Added as a synthetic user
    /// message so the model can access content the summary may have dropped. Respects the
    /// post-compaction budget: skipped when injection would bring the token count back up to or
    /// beyond the compaction threshold.
    /// </summary>
    public string? AdditionalContext { get; init; }

    /// <summary>A result carrying no additional context.</summary>
    public static PostCompactResult NoChange { get; } = new();
}
