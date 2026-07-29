namespace Coda.Agent.Hooks;

/// <summary>
/// The merged outcome of all <c>PreCompact</c> hook invocations, representing
/// whether to proceed with compaction and what summarisation instructions to use.
/// </summary>
public sealed record PreCompactResult
{
    /// <summary>
    /// When <see langword="true"/>, the compaction must be cancelled entirely. The caller must
    /// not retry immediately — the next compaction trigger (auto threshold or explicit command)
    /// will offer a fresh chance.
    /// </summary>
    public bool Block { get; init; }

    /// <summary>
    /// Replacement summarisation instructions. When non-null, overrides the default
    /// <c>CompactionPrompts.SystemPrompt</c> for this compaction run.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>The shell command of the hook that produced the block, or <see langword="null"/>.</summary>
    public string? ByHookCommand { get; init; }

    /// <summary>An allow result — compaction proceeds with the default instructions.</summary>
    public static PreCompactResult Allow { get; } = new();
}
