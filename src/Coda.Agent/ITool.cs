using System.Text.Json;
using Coda.Agent.Tasks;
using Coda.Agent.Lsp;
using Coda.Agent.Scheduling;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Coda.Agent;

/// <summary>Context passed to a tool execution (working dir + optional services).</summary>
public sealed record ToolContext(string WorkingDirectory)
{
    /// <summary>
    /// When true, filesystem tools may operate on paths outside <see cref="WorkingDirectory"/>.
    /// Set only in bypass-permissions ("yolo") mode; the default keeps the cwd sandbox.
    /// </summary>
    public bool AllowOutsideWorkingDirectory { get; init; }

    /// <summary>Telemetry logger for tool execution; null when telemetry is not wired (e.g. some tests).</summary>
    public ILogger? Logger { get; init; }

    /// <summary>The live agent sink (so tools like <c>task</c> can stream subagent output).</summary>
    public IAgentSink? Sink { get; init; }

    /// <summary>Correlation context for the current root turn's tool activity.</summary>
    public ToolActivityContext? ToolActivity { get; init; }

    /// <summary>Host that runs nested subagents (provided when the <c>task</c> tool is available).</summary>
    public ISubagentHost? Subagents { get; init; }

    /// <summary>Session todo list, when available (null for subagents).</summary>
    public TodoStore? Todos { get; init; }

    /// <summary>Scheduled-task store, when available (null for subagents).</summary>
    public ScheduledTaskStore? Schedules { get; init; }

    /// <summary>
    /// Host-neutral, read-only projection of the schedule runtime, when a runtime is active.
    /// Lets <c>schedule_list</c> report idle/running/pending state; null before the runtime
    /// starts or in subagent contexts.
    /// </summary>
    public IScheduleRuntimeView? ScheduleRuntime { get; init; }

    /// <summary>Interactive question prompt, when an interactive user is available (null for headless/subagents).</summary>
    public IUserQuestionPrompt? UserQuestion { get; init; }

    /// <summary>Plan-approval callback, when an interactive user is available (null for headless/subagents).</summary>
    public IPlanApprover? PlanApprover { get; init; }

    /// <summary>Task manager for subagent and shell tasks; null when not wired (e.g. some tests).</summary>
    public TaskManager? Tasks { get; init; }

    /// <summary>The current task's id when running inside a subagent task; null at the main agent (depth 0).</summary>
    public string? CurrentTaskId { get; init; }

    /// <summary>Nesting depth: 0 at the main agent, 1 for a child subagent, 2 for a grandchild.</summary>
    public int CurrentDepth { get; init; }

    /// <summary>
    /// The deepest nesting this session allows. Read from here rather than from a constant so a session
    /// can be configured to go deeper (or shallower) than the default.
    /// </summary>
    public int MaxSubagentDepth { get; init; } = Coda.Agent.Tasks.TaskManager.DefaultMaxSubagentDepth;

    /// <summary>LSP server manager for code-intelligence operations; null when no LSP servers are configured.</summary>
    public LspServerManager? Lsp { get; init; }

    /// <summary>The full registered tool set (deferred + non-deferred); null when not wired by the coordinator.</summary>
    public IReadOnlyList<ITool>? AllTools { get; init; }

    /// <summary>Callback invoked with matched tool names after a tool_search execution; null when not wired.</summary>
    public Action<IReadOnlyList<string>>? OnToolsDiscovered { get; init; }

    /// <summary>
    /// The tool restriction that was active for the parent turn, or <see langword="null"/> when
    /// no restriction is in effect. Passed to child subagents so they can inherit at least the
    /// same restriction, guaranteeing monotonic (never-less-restricted) security across nesting
    /// levels. Only the restriction portion of the parent's <see cref="TurnShape"/> is threaded
    /// through — system prompt, model, and effort overrides belong to the parent turn's context
    /// and must not leak into a child's independent agent context.
    /// </summary>
    public TurnShape? ParentToolRestriction { get; init; }

    /// <summary>
    /// Additional filesystem roots that file tools may access, beyond <see cref="WorkingDirectory"/>.
    /// Populated from skill directory-consent grants: when the user approves loading a skill from
    /// an external directory (e.g. <c>~/.coda/skills/foo</c>), that canonical directory is added
    /// here so the skill's bundled resource files can be read during the session.
    /// <see langword="null"/> means no additional roots are permitted (the default).
    /// </summary>
    public IReadOnlySet<string>? GrantedDirectories { get; init; }
}

/// <summary>The outcome of running a tool, fed back to the model.</summary>
/// <param name="Content">The text or serialised output returned to the model.</param>
/// <param name="IsError">When <see langword="true"/>, the model receives this as an error result.</param>
public sealed record ToolResult(string Content, bool IsError = false)
{
    /// <summary>
    /// Optional <see cref="TurnShape"/> delta that this tool wants applied to the current turn
    /// after its execution. The caller (typically <see cref="AgentLoop"/>) accumulates and
    /// layers deltas from all tools in a batch via <see cref="TurnShape.Layer"/>.
    /// <see langword="null"/> means no change to the current turn's shape.
    /// </summary>
    public TurnShape? ShapeDelta { get; init; }
}

/// <summary>An executable tool the model can call.</summary>
public interface ITool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema (as text) for the tool's input.</summary>
    string InputSchemaJson { get; }

    /// <summary>Read-only tools run without a permission prompt.</summary>
    bool IsReadOnly { get; }

    Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default);

    /// <summary>When true, this tool is hidden from the inline tool list and discovered on demand via tool_search.</summary>
    bool ShouldDefer => false;

    /// <summary>A curated capability phrase for search; null means rely on name and description alone.</summary>
    string? SearchHint => null;

    /// <summary>The wire definition advertised to the model.</summary>
    ToolDefinition ToDefinition() => new(this.Name, this.Description, this.InputSchemaJson);
}
