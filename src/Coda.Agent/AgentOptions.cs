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
}
