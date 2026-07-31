using System.Collections;
using Coda.Sdk;
using LlmClient;

namespace Coda.Tui.Repl;

/// <summary>Mutable per-session state (active provider, model, cwd, conversation).</summary>
public sealed class SessionState
{
    private readonly List<(int Label, ImageBlock Block, bool TokenInserted)> pendingLabeledImages = [];
    private readonly PendingImageAdapter pendingImages;

    public SessionState(string activeProviderId, string? workingDirectory = null)
    {
        this.ActiveProviderId = activeProviderId;
        this.WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
        this.pendingImages = new PendingImageAdapter(this);
    }

    public string ActiveProviderId { get; set; }

    public string WorkingDirectory { get; set; }

    /// <summary>
    /// The persisted session id this conversation writes to. Null until the first turn assigns one
    /// (captured back from the CodaSession) or a resume/continue seeds it. Settable by /resume.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>The model id used for chat (settable via /model).</summary>
    public string Model { get; set; } = AnthropicModels.DefaultModel;

    /// <summary>The CLI override supplied when this process started; authoritative over resumed metadata.</summary>
    public string? StartupSystemPromptOverride { get; init; }

    /// <summary>
    /// Set by <c>--yolo-safe</c>: in bypass mode every mutating action is first classified, so risky
    /// ones are escalated rather than blindly allowed.
    /// </summary>
    public bool EnableBypassClassifier { get; init; }

    /// <summary>The exact override currently applied to root turns; null uses normal construction.</summary>
    public string? SystemPromptOverride { get; set; }

    /// <summary>
    /// Stable, shared live permission state for this session. The same instance is passed into every
    /// turn's <see cref="SessionOptions.PermissionModeState"/>, so a <c>/yolo</c> or <c>/permissions</c>
    /// change applies to the next tool decision of the running loop and its subagents.
    /// </summary>
    public Coda.Agent.PermissionModeState PermissionModes { get; } =
        new(Coda.Agent.PermissionMode.Default);

    /// <summary>Tool-permission mode (settable via /permissions or /yolo). Delegates to <see cref="PermissionModes"/>.</summary>
    public Coda.Agent.PermissionMode PermissionMode
    {
        get => this.PermissionModes.Mode;
        set => this.PermissionModes.Mode = value;
    }

    /// <summary>Named output style persona (settable via /output-style).</summary>
    public string OutputStyle { get; set; } = "default";

    /// <summary>
    /// Reasoning effort level (low/medium/high/max), or null for the model
    /// default ("auto"). Settable via /effort. Session-scoped.
    /// </summary>
    public string? Effort { get; set; }

    /// <summary>
    /// In-memory cache of persisted effort levels, keyed by <c>"{provider}/{model}"</c>.
    /// Populated from <c>settings.json</c> at startup and updated by <c>/effort</c>.
    /// A lookup miss means "auto" (model default). The in-memory value is the
    /// authoritative source within a session; disk persistence keeps it durable.
    /// </summary>
    public Dictionary<string, string?> EffortByModel { get; } = new(StringComparer.OrdinalIgnoreCase);

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
    /// Images staged via /image or a clipboard paste that will be attached to the next user turn.
    /// Backward-compatible <see cref="IReadOnlyList{T}"/> view (with Add/Clear) over the labeled staging
    /// list; <see cref="Agent.AgentRunner"/> drains it after the turn is dispatched.
    /// </summary>
    public PendingImageAdapter PendingImages => this.pendingImages;

    /// <summary>
    /// The labeled staging list backing <see cref="PendingImages"/>. Each entry carries the display label
    /// (e.g. <c>[Image 1]</c>), the image block, and whether a token was inserted into the draft for it.
    /// </summary>
    public IReadOnlyList<(int Label, ImageBlock Block, bool TokenInserted)> PendingLabeledImages =>
        this.pendingLabeledImages;

    /// <summary>The next label to assign; starts at 1 and resets when staging is cleared.</summary>
    public int NextImageLabel { get; private set; } = 1;

    /// <summary>
    /// Stages an image, assigning the next sequential label. <paramref name="tokenInserted"/> records
    /// whether an <c>[Image N]</c> token was written into the draft for it (paste and interactive /image),
    /// which governs the token-scan attachment policy in <see cref="Agent.AgentRunner"/>. Returns the label.
    /// </summary>
    public int StageImage(ImageBlock block, bool tokenInserted = false)
    {
        var label = this.NextImageLabel++;
        this.pendingLabeledImages.Add((label, block, tokenInserted));
        return label;
    }

    /// <summary>Clears all staged images and resets the label counter to 1.</summary>
    public void ClearStagedImages()
    {
        this.pendingLabeledImages.Clear();
        this.NextImageLabel = 1;
    }

    /// <summary>Active autonomous goal (settable via /goal); null = no goal. Persists across turns until cleared.</summary>
    public string? Goal { get; set; }

    /// <summary>Per-goal wall-clock budget override (/goal --timeout); null = settings/default.</summary>
    public TimeSpan? GoalMaxDuration { get; set; }

    /// <summary>Per-goal turn backstop override (/goal --max-turns); null = settings/default.</summary>
    public int? GoalMaxContinuations { get; set; }
}

/// <summary>
/// Backward-compatible <see cref="IReadOnlyList{T}"/> facade over <see cref="SessionState"/>'s labeled image
/// staging list. Preserves the historical <c>PendingImages.Add</c>/<c>.Clear</c>/indexer/enumeration surface
/// while delegating to <see cref="SessionState.StageImage"/> and <see cref="SessionState.ClearStagedImages"/>.
/// </summary>
public sealed class PendingImageAdapter : IReadOnlyList<ImageBlock>
{
    private readonly SessionState owner;

    internal PendingImageAdapter(SessionState owner) => this.owner = owner;

    public int Count => this.owner.PendingLabeledImages.Count;

    public ImageBlock this[int index] => this.owner.PendingLabeledImages[index].Block;

    /// <summary>Stages an image with no draft token (legacy /image behaviour).</summary>
    public void Add(ImageBlock block) => this.owner.StageImage(block, tokenInserted: false);

    /// <summary>Clears all staged images and resets the label counter.</summary>
    public void Clear() => this.owner.ClearStagedImages();

    public IEnumerator<ImageBlock> GetEnumerator()
    {
        foreach (var entry in this.owner.PendingLabeledImages)
        {
            yield return entry.Block;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
}
