using LlmClient;

namespace Coda.Agent;

/// <summary>Configuration for an <see cref="AgentLoop"/> run.</summary>
public sealed record AgentOptions
{
    public string Model { get; init; } = AnthropicModels.DefaultModel;

    public required string SystemPrompt { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// The permission mode for this run. In <see cref="PermissionMode.BypassPermissions"/>
    /// ("yolo") the filesystem tools may operate outside the working directory.
    /// </summary>
    public PermissionMode PermissionMode { get; init; } = PermissionMode.Default;

    /// <summary>
    /// Optional shared, live permission-mode state. When supplied, per-tool-request decisions
    /// (including the filesystem sandbox) read the current mode from it so a mid-run <c>/yolo</c>
    /// or <c>/permissions</c> change is applied immediately to a still-running loop and its
    /// subagents. When null (a fixed headless run) the snapshot <see cref="PermissionMode"/> is used.
    /// </summary>
    public PermissionModeState? PermissionModeState { get; init; }

    /// <summary>
    /// High backstop on tool-use iterations per user turn — a runaway-loop guard, not a work budget.
    /// Hitting it is a recoverable soft stop (the turn ends and the session returns to idle), not a crash.
    /// </summary>
    public int MaxIterations { get; init; } = 500;

    /// <summary>
    /// Safety bound on how many times stop hooks may force the agent to continue
    /// after it tries to finish. Prevents a runaway "never stop" hook.
    /// </summary>
    public int MaxStopContinuations { get; init; } = 10;

    /// <summary>
    /// The per-response output-token ceiling sent as the request's <c>max_tokens</c>. The SDK normally sets
    /// this to the selected model's REAL published output limit from the model catalog (see
    /// <c>ModelLimits.ResolveMaxOutputTokens</c>) so it never exceeds the model's cap. This default is only
    /// a conservative fallback for direct construction without that resolution.
    /// </summary>
    public int MaxTokens { get; init; } = 8192;

    /// <summary>
    /// Reasoning effort level (low/medium/high/max), or null for the model
    /// default. Forwarded to the model request; honored only by models that
    /// support effort.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>Compact the running history mid-loop when it exceeds the threshold. Default on.</summary>
    public bool AutoCompact { get; init; } = true;

    /// <summary>Estimated-token threshold above which the loop compacts mid-run. Resolved from the
    /// model's context window upstream (CodaSession); 0 here means none was resolved (no compaction).</summary>
    public int AutoCompactTokenThreshold { get; init; } = 0;

    /// <summary>
    /// The fully-resolved system prompt from the previous user turn (base + any appends from
    /// <see cref="TurnShape.AppendSystemPrompt"/> or session-level hooks), or <see langword="null"/>
    /// on the first turn. Used by <see cref="AgentLoop"/> to detect when the resolved system prompt
    /// changed between turns so it can log a debug note that the prompt-cache prefix shifted.
    /// A change means the model will write a fresh cache entry rather than reading an existing one.
    /// Callers must store the resolved value (not just the base) so a stable per-turn append does
    /// not produce a false-positive prefix-change log on every subsequent turn.
    /// </summary>
    public string? PreviousSystemPrompt { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the stable prefix breakpoints (tools and system prompt) use a
    /// 1-hour TTL (<c>{"type":"ephemeral","ttl":"1h"}</c>) instead of the default 5-minute TTL.
    /// The longer TTL is useful when a human-in-the-loop pause between turns may exceed five minutes.
    /// A 1-hour write costs 2× the base input rate versus 1.25× for 5-minute, so this is opt-in.
    /// Message breakpoints always use the 5-minute TTL regardless of this setting.
    /// Default: <see langword="false"/>.
    /// </summary>
    public bool UseOnehourTtl { get; init; }
}
