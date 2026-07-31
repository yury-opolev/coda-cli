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

    /// <summary>The values in force when nothing is configured, matching the historic constants.</summary>
    public static SubagentSettings Default { get; } = new();

    private readonly int maxDepth = 2;
    private readonly int maxConcurrent = 8;

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
    public bool AllowSystemPromptReplacement { get; init; }
}
