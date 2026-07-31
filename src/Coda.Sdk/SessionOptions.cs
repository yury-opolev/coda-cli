using Coda.Agent;
using Coda.Agent.OutputStyles;
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

    /// <summary>
    /// Optional callback invoked after in-loop or pre-turn compaction to obtain skill body content
    /// that should be re-injected into history. The integer argument is the resolved
    /// <see cref="AutoCompactTokenThreshold"/> for this turn; the lambda computes the character
    /// budget via <c>SkillSessionState.DeriveReattachBudget</c> and calls
    /// <c>SkillSessionState.GetReattachContent</c>. Returns the content to inject, or null/empty
    /// when nothing needs re-injecting. Wires the <c>Coda.Tui.Skills.SkillSessionState</c>
    /// into the compaction path without creating a dependency on <c>Coda.Tui</c> in this assembly.
    /// </summary>
    public Func<int, string>? SkillReattachContentProvider { get; init; }

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

    /// <summary>
    /// Session-scoped plugin output styles for this session. Checked before the static process-global
    /// registry so serve sessions with different working directories resolve only their own plugin styles.
    /// Empty by default (no plugin styles); the TUI/headless paths may populate this from
    /// <c>Coda.Tui.Plugins.PluginComposition.OutputStyles</c> at session construction.
    /// </summary>
    public IReadOnlyList<OutputStyle> PluginOutputStyles { get; init; } = [];

    /// <summary>
    /// Plugin-contributed LSP servers, keyed by the loader's scoped name. Merged beneath the settings
    /// servers, which win on a key clash.
    /// </summary>
    /// <remarks>
    /// Supplied by the caller rather than discovered here, because starting an LSP server runs a
    /// process and only the plugin layer knows which plugins the user enabled and approved for it
    /// (<c>Coda.Tui.Plugins.PluginComposition.LspServers</c>). Empty by default, so a host that
    /// does not compose plugins contributes none rather than silently scanning for them.
    /// </remarks>
    public IReadOnlyDictionary<string, Coda.Agent.Lsp.LspServerConfig> PluginLspServers { get; init; } =
        new Dictionary<string, Coda.Agent.Lsp.LspServerConfig>(StringComparer.Ordinal);

    /// <summary>
    /// Limits on subagent nesting and fan-out, and whether the main agent may replace a subagent's
    /// system prompt. Null (the default) resolves the <c>subagents</c> block from the session's own
    /// settings files, so a host does not have to remember to pass it; set it only to override the
    /// settings for this session.
    /// </summary>
    public Coda.Agent.Settings.SubagentSettings? SubagentSettings { get; init; }

    /// <summary>
    /// Complete exact root system prompt. Null uses normal Coda construction; empty and whitespace are exact values.
    /// </summary>
    public string? SystemPromptOverride { get; init; }

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

    /// <summary>
    /// Optional session source label for the <c>SessionStart</c> hook payload. When set, this
    /// value is emitted as the <c>source</c> field instead of the default "new". Callers that
    /// create sessions for a specific lifecycle context (e.g. a scheduled run) pass
    /// <c>"scheduled"</c> here so hooks can distinguish those sessions from interactive ones.
    /// Null emits "new" (the default). "resume" is set automatically when
    /// <see cref="CodaSession.Resume"/> is called, regardless of this property.
    /// </summary>
    public string? SessionSource { get; init; }

    /// <summary>
    /// Factory that returns the current set of additional filesystem roots the file tools may
    /// access beyond <see cref="WorkingDirectory"/>. Invoked per tool-batch so grants made
    /// mid-session take effect on the next batch. Null means no additional roots.
    /// Populated from <c>SkillSessionState.GetGrantedDirectories</c> at composition time.
    /// </summary>
    public Func<IReadOnlySet<string>?>? GrantedDirectoriesSource { get; init; }
}
