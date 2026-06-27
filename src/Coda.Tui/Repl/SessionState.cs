using Coda.Sdk;
using LlmClient;

namespace Coda.Tui.Repl;

/// <summary>Mutable per-session state (active provider, model, cwd, conversation).</summary>
public sealed class SessionState
{
    public SessionState(string activeProviderId, string? workingDirectory = null)
    {
        this.ActiveProviderId = activeProviderId;
        this.WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
    }

    public string ActiveProviderId { get; set; }

    public string WorkingDirectory { get; set; }

    /// <summary>The model id used for chat (settable via /model).</summary>
    public string Model { get; set; } = AnthropicModels.DefaultModel;

    /// <summary>Tool-permission mode (settable via /permissions or /yolo).</summary>
    public Coda.Agent.PermissionMode PermissionMode { get; set; } = Coda.Agent.PermissionMode.Default;

    /// <summary>Named output style persona (settable via /output-style).</summary>
    public string OutputStyle { get; set; } = "default";

    /// <summary>
    /// Reasoning effort level (low/medium/high/max), or null for the model
    /// default ("auto"). Settable via /effort. Session-scoped.
    /// </summary>
    public string? Effort { get; set; }

    /// <summary>The running conversation (grows across turns; cleared by /clear).</summary>
    public List<ChatMessage> History { get; } = [];

    /// <summary>Accumulated token usage for the current session (updated by AgentRunner after each run).</summary>
    public TokenUsage SessionUsage { get; set; } = TokenUsage.Zero;

    /// <summary>
    /// Per-provider cache of the resolved model list (from <c>/model</c>), so repeated
    /// listings don't re-hit the network within a session. Cleared by /model refresh.
    /// </summary>
    public Dictionary<string, Coda.Sdk.ModelListResult> ModelListCache { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Images staged via /image that will be attached to the next user turn.
    /// Cleared by <see cref="Agent.AgentRunner"/> after the turn is dispatched.
    /// </summary>
    public List<ImageBlock> PendingImages { get; } = [];

    /// <summary>Active autonomous goal (settable via /goal); null = no goal. Persists across turns until cleared.</summary>
    public string? Goal { get; set; }

    /// <summary>Per-goal wall-clock budget override (/goal --timeout); null = settings/default.</summary>
    public TimeSpan? GoalMaxDuration { get; set; }

    /// <summary>Per-goal turn backstop override (/goal --max-turns); null = settings/default.</summary>
    public int? GoalMaxContinuations { get; set; }
}
