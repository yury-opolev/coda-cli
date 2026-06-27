using Coda.Agent;
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Goals;
using Coda.Agent.Hooks;
using Coda.Agent.Lsp;
using Coda.Agent.Scheduling;
using Coda.Agent.Teams;
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
/// <param name="BackgroundTasks">Optional runner for background (detached) tasks.</param>
/// <param name="Lsp">Optional language-server manager for LSP-backed tools.</param>
/// <param name="LspDiagnostics">Optional registry of LSP diagnostics surfaced to the model.</param>
/// <param name="Teams">Optional team manager backing the team/teammate tools.</param>
/// <param name="ToolSearch">Optional coordinator backing the tool-search tool.</param>
/// <param name="Goal">Optional goal supervisor governing long, goal-driven runs.</param>
/// <param name="CompactAsync">Optional in-loop compaction callback for goal runs.</param>
/// <param name="Logger">Logger for the loop's tool/turn diagnostics.</param>
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
    BackgroundTaskRunner? BackgroundTasks,
    LspServerManager? Lsp,
    LspDiagnosticRegistry? LspDiagnostics,
    TeamManager? Teams,
    ToolSearchCoordinator? ToolSearch,
    GoalSupervisor? Goal,
    Func<List<ChatMessage>, CancellationToken, Task>? CompactAsync,
    ILogger Logger,
    SteeringInbox? Steering = null);
