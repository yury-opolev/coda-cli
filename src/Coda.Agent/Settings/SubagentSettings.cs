namespace Coda.Agent.Settings;

/// <summary>
/// Limits on how far and how wide a session may fan out into subagents, and whether the main agent
/// may replace a subagent's system prompt outright.
/// </summary>
/// <remarks>
/// These are settings rather than constants because the right values depend on the work: a shallow
/// refactor needs neither depth nor fan-out, an architectural survey wants both. Every value is
/// clamped on construction, so a settings file — which is exactly what an attacker who can write to
/// the project would edit — can raise a limit within reason but never remove one.
/// </remarks>
public sealed record SubagentSettings
{
    /// <summary>The deepest nesting a settings file may ask for.</summary>
    public const int MaxAllowedDepth = 10;

    /// <summary>The widest fan-out a settings file may ask for.</summary>
    public const int MaxAllowedConcurrent = 64;

    /// <summary>The values in force when nothing is configured.</summary>
    public static SubagentSettings Default { get; } = new();

    private readonly int maxDepth = 2;
    private readonly int maxConcurrent = 20;
    private readonly string? model;

    /// <summary>
    /// Deepest subagent nesting. The main agent is depth 0, so 2 permits a subagent and a grandchild.
    /// Clamped to <c>[1, <see cref="MaxAllowedDepth"/>]</c>.
    /// </summary>
    public int MaxDepth
    {
        get => this.maxDepth;
        init => this.maxDepth = Math.Clamp(value, 1, MaxAllowedDepth);
    }

    /// <summary>
    /// How many subagent tasks may run at once across the session, foreground and background together.
    /// Clamped to <c>[1, <see cref="MaxAllowedConcurrent"/>]</c>.
    /// </summary>
    public int MaxConcurrent
    {
        get => this.maxConcurrent;
        init => this.maxConcurrent = Math.Clamp(value, 1, MaxAllowedConcurrent);
    }

    /// <summary>
    /// Whether the main agent may replace a subagent's system prompt entirely, rather than only append
    /// to it. Off by default: appending leaves the subagent's own identity and guardrails in front of
    /// whatever the caller adds, and replacing removes them.
    /// </summary>
    /// <remarks>
    /// Settable from the user settings file only. A project file is attacker-controlled as soon as
    /// someone clones a hostile repo, and unlike the clamped depth and fan-out limits this one
    /// decides whether a prompt-injected model can hand a subagent its instructions.
    /// </remarks>
    public bool AllowSystemPromptReplacement { get; init; }

    /// <summary>
    /// Global model override for all subagents, applied when no per-type or per-request model is
    /// specified. Null or blank means use the session model (today's behaviour).
    /// </summary>
    /// <remarks>
    /// Settable from the user settings file only. A project settings file is attacker-controlled the
    /// moment someone clones a hostile repo, and model choice is a cost lever: a hostile project
    /// pinning every subagent to the most expensive model is a real escalation.
    /// maxDepth/maxConcurrent keep their existing project-wins merge because they are clamped
    /// resource bounds.
    /// </remarks>
    public string? Model
    {
        get => this.model;
        init => this.model = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Per-type model overrides, keyed by subagent type (e.g. <c>"explore"</c>). Ordinal-ignore-case
    /// comparison. Takes precedence over <see cref="Model"/> for matching types.
    /// </summary>
    /// <remarks>
    /// Settable from the user settings file only, for the same reason as <see cref="Model"/>.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ModelByType { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
