using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Goals;
using Coda.Agent.Hooks;
using Coda.Agent.Lsp;
using Coda.Agent.Scheduling;
using Coda.Agent.ToolSearch;
using Coda.Agent.Tools;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Coda.Sdk;

/// <summary>
/// Parameter object bundling exactly the arguments a per-turn <see cref="AgentLoop"/> is
/// constructed with in <see cref="CodaSession.RunAsync(System.Collections.Generic.IReadOnlyList{ContentBlock}, IAgentSink?, System.Threading.CancellationToken)"/>.
/// Defines the seam an <see cref="IAgentLoopFactory"/> maps to a loop, taming the long
/// constructor argument list and giving later refactors a single place to populate.
/// </summary>
/// <param name="Client">The provider chat client the loop streams turns from.</param>
/// <param name="Tools">The tool registry exposed to the model for the turn.</param>
/// <param name="Permissions">The permission policy gating mutating tool calls.</param>
/// <param name="Options">The agent options (model, system prompt, bounds, effort).</param>
/// <param name="Subagents">Optional host for spawning subagents via the task tool.</param>
/// <param name="Hooks">Optional post-sampling observe-bus and stop-hook levers.</param>
/// <param name="Todos">Optional shared todo store across the session.</param>
/// <param name="Schedules">Optional scheduled-task store for the schedule tools.</param>
/// <param name="UserQuestion">Optional prompt used when a tool asks the user a question.</param>
/// <param name="UserHooks">Optional user-configured hook runner from settings.</param>
/// <param name="PlanApprover">Optional approver consulted for plan-mode turns.</param>
/// <param name="Lsp">Optional language-server manager for LSP-backed tools.</param>
/// <param name="LspDiagnostics">Optional registry of LSP diagnostics surfaced to the model.</param>
/// <param name="ToolSearch">Optional coordinator backing the tool-search tool.</param>
/// <param name="Goal">Optional goal supervisor governing long, goal-driven runs.</param>
/// <param name="CompactAsync">Optional in-loop compaction callback for goal runs.</param>
/// <param name="Logger">Logger for the loop's tool/turn diagnostics.</param>
/// <param name="Steering">Optional steering inbox for mid-turn redirection.</param>
/// <param name="PersistTurnAsync">Optional callback invoked after each assistant turn and
/// tool cycle so the transcript is recorded incrementally ("on the go") — a session killed
/// mid-run then still leaves a record of everything up to the kill.</param>
/// <param name="Tasks">Task manager owning subagent and shell tasks (parallel to the legacy runner during migration).</param>
/// <param name="Gate">Optional cooperative execution gate letting an outside actor pause the main
/// agent at an iteration boundary and resume it. Null in serve/headless runs where no pause is requested.</param>
/// <param name="ScheduleRuntime">Optional host-neutral runtime-state view surfaced to the schedule
/// tools so <c>schedule_list</c> can report idle/running/pending. Null before the runtime starts.</param>
/// <param name="CurrentTaskId">The running task's id when the loop is a scheduled root or subagent
/// task; null for the main agent. Threaded to the tool <see cref="ToolContext"/> so task lifecycle
/// tools resolve descendant authorization from trusted context.</param>
/// <param name="CurrentDepth">Nesting depth of the loop: 0 at the main agent, 1 for a scheduled
/// root or first-level subagent, 2 for a grandchild. Bounds further subagent nesting.</param>
public sealed record AgentLoopSpec(
    ILlmClient Client,
    ToolRegistry Tools,
    IPermissionPrompt Permissions,
    AgentOptions Options,
    ISubagentHost? Subagents,
    AgentHooks? Hooks,
    TodoStore? Todos,
    ScheduledTaskStore? Schedules,
    IUserQuestionPrompt? UserQuestion,
    UserHookRunner? UserHooks,
    IPlanApprover? PlanApprover,
    LspServerManager? Lsp,
    LspDiagnosticRegistry? LspDiagnostics,
    ToolSearchCoordinator? ToolSearch,
    GoalSupervisor? Goal,
    Func<List<ChatMessage>, CancellationToken, Task>? CompactAsync,
    ILogger Logger,
    SteeringInbox? Steering = null,
    Func<CancellationToken, Task>? PersistTurnAsync = null,
    TaskManager? Tasks = null,
    AgentExecutionGate? Gate = null,
    IScheduleRuntimeView? ScheduleRuntime = null,
    string? CurrentTaskId = null,
    int CurrentDepth = 0)
{
    /// <summary>
    /// Root tool-activity identity owned by the host for this loop invocation. The loop derives
    /// a single activity id from it when it executes the first tool batch.
    /// </summary>
    public ToolActivityContext? ToolActivity { get; init; }
}
