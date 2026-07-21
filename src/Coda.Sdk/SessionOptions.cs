using Coda.Agent;
using Coda.Agent.Settings;
using LlmClient;

namespace Coda.Sdk;

/// <summary>Configuration for a <see cref="CodaSession"/>.</summary>
public sealed record SessionOptions
{
    public required string ProviderId { get; init; }

    public required string Model { get; init; }

    public required string WorkingDirectory { get; init; }

    public PermissionMode PermissionMode { get; init; } = PermissionMode.Default;

    /// <summary>
    /// Optional shared, live permission state. When supplied, the per-turn permission prompt reads
    /// the current mode from it on every decision, so a mid-run <c>/yolo</c> or <c>/permissions</c>
    /// change affects the next tool decision of this loop and every subagent that shares it. When
    /// null (headless/serve), a fixed state is derived from <see cref="PermissionMode"/>.
    /// </summary>
    public PermissionModeState? PermissionModeState { get; init; }

    /// <summary>Extra tools beyond the built-ins (e.g. MCP tools).</summary>
    public IReadOnlyList<ITool> ExtraTools { get; init; } = [];

    /// <summary>Interactive prompt used when the mode decides to Ask. Null = headless (Ask denies).</summary>
    public IPermissionPrompt? InteractivePrompt { get; init; }

    /// <summary>
    /// High backstop on tool-use iterations per user turn. Not a budget — a runaway-loop guard.
    /// Hitting it is a recoverable soft stop (the turn ends and the session returns to idle), not a crash.
    /// </summary>
    public int MaxIterations { get; init; } = 500;

    /// <summary>
    /// Optional per-session override for the request's <c>max_tokens</c>. Null (the default) means use the
    /// selected model's REAL published output ceiling from the model catalog, resolved per turn by
    /// <see cref="ModelLimits.ResolveMaxOutputTokens"/>. When set, the override is honored but clamped to
    /// the model's real ceiling so it can never exceed it (which the Anthropic API rejects with a 400).
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>Run the SessionMemory watcher (a background notes file) after work-bearing turns.</summary>
    public bool EnableSessionMemory { get; init; }

    /// <summary>
    /// When set, <see cref="CodaSession.InitializeAsync"/> creates and starts the session-owned
    /// schedule runtime so due scheduled definitions fire as isolated agent runs. Off by default:
    /// headless and other SDK callers stay unchanged until an interactive/serve host opts in.
    /// </summary>
    public bool EnableScheduleRuntime { get; init; }

    /// <summary>In bypass mode, classify each tool action and escalate the risky ones instead of blanket-allowing.</summary>
    public bool EnableBypassClassifier { get; init; }

    /// <summary>When set, an autonomous goal: a stop hook keeps the agent working until a judge says it is met.</summary>
    public string? Goal { get; init; }

    /// <summary>Bound on stop-hook forced continuations per run. Active only when no goal is set; goal runs are bounded by the goal budget (GoalMaxContinuations / GoalMaxDuration) instead.</summary>
    public int MaxStopContinuations { get; init; } = 10;

    /// <summary>Estimated-token threshold above which the conversation is auto-summarized before a turn.
    /// 0 (default) = derive from the model's context window (see <c>ModelLimits.ResolveAutoCompactThreshold</c>);
    /// an explicit positive value overrides.</summary>
    public int AutoCompactTokenThreshold { get; init; } = 0;

    /// <summary>Interactive question prompt, when an interactive user is available. Null for headless sessions.</summary>
    public IUserQuestionPrompt? UserQuestionPrompt { get; init; }

    /// <summary>Plan-approval callback, when an interactive user is available. Null for headless sessions.</summary>
    public IPlanApprover? PlanApprover { get; init; }

    /// <summary>Named output style persona (e.g. "concise", "explanatory", "code-reviewer"). Null or "default" = no change.</summary>
    public string? OutputStyle { get; init; }

    /// <summary>Reasoning effort level (low/medium/high/max), or null for the model default. Honored only by models that support effort.</summary>
    public string? Effort { get; init; }

    /// <summary>Wall-clock budget for a goal run. Null → settings/default (24h).</summary>
    public TimeSpan? GoalMaxDuration { get; init; }

    /// <summary>Turn (continuation) backstop for a goal run. Null → settings/default (60000).</summary>
    public int? GoalMaxContinuations { get; init; }

    /// <summary>
    /// When set, overrides the settings-file telemetry block for this session only (e.g. <c>coda serve --telemetry</c>
    /// forces logging on regardless of <c>~/.coda/settings.json</c>). Null = use the loaded settings. Never written to disk.
    /// </summary>
    public TelemetrySettings? TelemetryOverride { get; init; }

    /// <summary>
    /// When set, overrides the HTTP-layer hung-call guards (response-headers + stream-idle
    /// timeouts) for this session's LLM clients. Null = resolve from the environment
    /// (<see cref="LlmHttpTimeoutConfig.FromEnvironment()"/>). A hung LLM call is bounded
    /// here, inside the client — not by any turn-level watchdog.
    /// </summary>
    public LlmHttpTimeoutConfig? LlmHttpTimeoutOverride { get; init; }
}
