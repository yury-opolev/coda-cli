using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Classifier;
using Coda.Agent.Compaction;
using Coda.Agent.Goals;
using Coda.Agent.Hooks;
using Coda.Agent.Lsp;
using Coda.Agent.Permissions;
using Coda.Agent.Scheduling;
using Coda.Agent.Settings;
using Coda.Agent.ToolSearch;
using Coda.Agent.Tools;
using Coda.Agent.Watchers;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Coda.Sdk.Turns;

/// <summary>
/// Owns the per-turn assembly that turns a <see cref="SessionOptions"/> snapshot, a resolved
/// provider <see cref="ILlmClient"/>, and the loaded <see cref="CodaSettings"/> into the
/// <see cref="AgentLoopSpec"/> a turn runs against.
/// </summary>
/// <remarks>
/// Extracted from <c>CodaSession.RunAsync</c> so the ~120-line assembly is a focused,
/// independently-testable unit. The builder holds the session's STABLE collaborators (stores,
/// LSP/tool-search managers, the logger factory, and a compaction delegate) and is
/// constructed once per session; only the per-turn <see cref="BuildSpec"/> inputs vary. Each
/// private step has a single responsibility and is exercised in isolation by tests.
///
/// Behaviour is byte-identical to the former inline assembly for every option combination — see
/// the characterization tests under <c>tests/Engine.Tests/Sdk/Turns</c>.
/// </remarks>
public sealed class TurnPipelineBuilder
{
    private readonly TodoStore todos;
    private readonly ScheduledTaskStore schedules;
    private readonly TaskManager tasks;
    private readonly LspServerManager? lspManager;
    private readonly LspDiagnosticRegistry? lspDiagnostics;
    private readonly ToolSearchCoordinator? toolSearchCoordinator;
    private readonly ILoggerFactory loggerFactory;
    private readonly Func<ILlmClient, string, string, IAgentSink?, CancellationToken, Task<bool>> compactHistoryAsync;
    private readonly Func<IScheduleRuntimeView?> scheduleRuntimeProvider;

    // Session-stable hook collaborators. When provided, these replace the per-turn
    // settings.Hooks reload so enable/disable changes take effect without a restart,
    // and the run log is shared with HookManagementService for /hooks info.
    private readonly List<UserHook>? sessionHookList;
    private readonly HookRunLog? runLog;
    private readonly HookTrustGuard? trustGuard;

    /// <summary>
    /// Creates the builder with the session's stable per-session collaborators. These do not
    /// change between turns, so the builder is constructed once in the session ctor.
    /// </summary>
    /// <param name="todos">Shared todo store across the session.</param>
    /// <param name="schedules">Scheduled-task store backing the schedule tools.</param>
    /// <param name="tasks">Task manager owning subagent and shell tasks.</param>
    /// <param name="lspManager">Language-server manager, or null when no LSP servers are configured.</param>
    /// <param name="lspDiagnostics">Diagnostics registry paired with <paramref name="lspManager"/>, or null.</param>
    /// <param name="toolSearchCoordinator">Coordinator backing the tool-search tool, or null in Standard mode.</param>
    /// <param name="loggerFactory">Factory for the loop's tool/turn loggers.</param>
    /// <param name="compactHistoryAsync">
    /// Compaction delegate bound to the session's in-place history compaction
    /// (<c>CodaSession.CompactHistoryAsync</c>); invoked by the goal-run compact callback.
    /// Returns <see langword="true"/> when compaction actually ran, <see langword="false"/> when
    /// it was blocked by a <c>PreCompact</c> hook or skipped because history was empty.
    /// </param>
    /// <param name="scheduleRuntimeProvider">
    /// Stable accessor for the session's schedule runtime-state view. Evaluated on every
    /// <see cref="BuildSpec"/> call (not captured once) because the runtime is created after the
    /// builder — it returns null until the runtime starts, then the live view.
    /// </param>
    /// <param name="sessionHookList">
    /// Mutable hook list frozen at session start. When provided, <see cref="BuildSpec"/> uses this
    /// list instead of reloading from settings each turn, so <c>/hooks enable/disable</c> takes
    /// effect immediately. Must be the same instance given to <c>HookManagementService</c>.
    /// </param>
    /// <param name="runLog">
    /// Session-scoped run log shared with <c>HookManagementService</c> for <c>/hooks info</c>.
    /// </param>
    /// <param name="trustGuard">
    /// Trust guard for project-scoped hooks. When non-null, every project-scoped hook is
    /// checked before execution; untrusted hooks are blocked per their fail-open/closed policy.
    /// </param>
    public TurnPipelineBuilder(
        TodoStore todos,
        ScheduledTaskStore schedules,
        TaskManager tasks,
        LspServerManager? lspManager,
        LspDiagnosticRegistry? lspDiagnostics,
        ToolSearchCoordinator? toolSearchCoordinator,
        ILoggerFactory loggerFactory,
        Func<ILlmClient, string, string, IAgentSink?, CancellationToken, Task<bool>> compactHistoryAsync,
        Func<IScheduleRuntimeView?> scheduleRuntimeProvider,
        List<UserHook>? sessionHookList = null,
        HookRunLog? runLog = null,
        HookTrustGuard? trustGuard = null)
    {
        this.todos = todos ?? throw new ArgumentNullException(nameof(todos));
        this.schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        this.tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        this.lspManager = lspManager;
        this.lspDiagnostics = lspDiagnostics;
        this.toolSearchCoordinator = toolSearchCoordinator;
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        this.compactHistoryAsync = compactHistoryAsync ?? throw new ArgumentNullException(nameof(compactHistoryAsync));
        this.scheduleRuntimeProvider = scheduleRuntimeProvider ?? throw new ArgumentNullException(nameof(scheduleRuntimeProvider));
        this.sessionHookList = sessionHookList;
        this.runLog = runLog;
        this.trustGuard = trustGuard;
    }

    /// <summary>
    /// Assembles the <see cref="AgentLoopSpec"/> for one turn from the per-turn inputs. Orchestrates
    /// the private steps in the same order and with the same data flow as the former inline assembly,
    /// so the produced spec is field-for-field identical.
    /// </summary>
    /// <param name="options">The session options snapshot for this turn.</param>
    /// <param name="client">The resolved provider chat client for this turn.</param>
    /// <param name="settings">The settings loaded once by the caller (never re-loaded here).</param>
    public AgentLoopSpec BuildSpec(SessionOptions options, ILlmClient client, CodaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(settings);

        var includeAnthropicSystemPrefix = options.ProviderId != GitHubCopilotProvider.Id;

        var agentOptions = this.BuildAgentOptions(options);

        var (permissions, permissionRules) = BuildPermissions(options, client, settings);

        // The goal step may mutate agentOptions (AutoCompact + threshold) when a goal is active.
        var (goalSupervisor, goalAgentOptions) = BuildGoalSupervisor(options, client, settings, agentOptions);
        agentOptions = goalAgentOptions;

        // Note: SessionMemory post-sampling hook writes notes in background; if the turn is later
        // rolled back on error, the notes file may still reflect the rolled-back turn — this is
        // acceptable because the notes file is advisory and idempotent.
        var hooks = BuildHooks(client, options);

        // HookContext supplies the common envelope written into every hook payload.
        // SessionId comes from the TaskManager (the stable per-session identifier).
        // Depth and task id are per-invocation and supplied by AgentLoop at each hook call site.
        var hookContext = new HookContext(
            SessionId: this.tasks.SessionId,
            Cwd: agentOptions.WorkingDirectory);
        // Use the session-frozen hook list when provided (supports /hooks enable/disable);
        // fall back to the per-turn settings load for backward-compat when no list is injected.
        var hookList = (IReadOnlyList<UserHook>?)this.sessionHookList ?? settings.Hooks;
        var (httpHandler, promptHandler, agentHandler) = hookList.Count > 0
            ? this.BuildHookHandlers(client, settings, options, agentOptions, permissions, includeAnthropicSystemPrefix)
            : default;
        var userHooks = hookList.Count > 0
            ? new UserHookRunner(hookList, context: hookContext,
                logger: this.loggerFactory.CreateLogger("Coda.Hooks"),
                httpHandler: httpHandler, promptHandler: promptHandler, agentHandler: agentHandler,
                trustGuard: this.trustGuard, runLog: this.runLog)
            : null;

        var subagentHost = BuildSubagentHost(options, client, agentOptions, permissions, includeAnthropicSystemPrefix, userHooks, this.tasks);

        var parentTools = this.BuildParentTools(options);

        return new AgentLoopSpec(
            client,
            parentTools,
            permissions,
            agentOptions,
            subagentHost,
            hooks,
            Todos: this.todos,
            Schedules: this.schedules,
            UserQuestion: options.UserQuestionPrompt,
            UserHooks: userHooks,
            PlanApprover: options.PlanApprover,
            Tasks: this.tasks,
            Lsp: this.lspManager,
            LspDiagnostics: this.lspDiagnostics,
            ToolSearch: this.toolSearchCoordinator,
            Goal: goalSupervisor,
            // The loop runs on the session history, which the compaction delegate compacts in
            // place. When a skill-reattach provider is configured, the reattach content is
            // injected into history immediately after compaction so the model does not lose
            // previously loaded skill bodies. The history list argument IS the live session
            // history shared with CodaSession, so mutations are visible after the await.
            CompactAsync: goalSupervisor is null
                ? null
                : BuildCompactDelegate(client, options),
            Logger: this.loggerFactory.CreateLogger("Coda.Tool"),
            // Evaluated per turn so a runtime that starts after the builder was constructed is
            // picked up on the next turn; returns null until then.
            ScheduleRuntime: this.scheduleRuntimeProvider())
        {
            PermissionRules = permissionRules,
            GrantedDirectoriesSource = options.GrantedDirectoriesSource,
        };
    }

    /// <summary>
    /// Builds the in-loop compaction delegate. After running the session's in-place history
    /// compaction, optionally injects skill-reattach content so a compacted goal run does not
    /// silently lose skills the model already loaded.
    /// </summary>
    /// <remarks>
    /// Two guards prevent reattach from eroding the headroom compaction just freed:
    /// <list type="bullet">
    ///   <item>Skip injection when adding the reattach content would bring the post-compaction
    ///     token estimate back up to or beyond the compaction threshold.</item>
    ///   <item>Skip injection when the reattach content is already the trailing message
    ///     (exactly-once guarantee — prevents double-injection when this delegate fires
    ///     consecutively without intervening turns).</item>
    /// </list>
    /// </remarks>
    private Func<List<ChatMessage>, IAgentSink, CancellationToken, Task<bool>> BuildCompactDelegate(
        ILlmClient client,
        SessionOptions options)
    {
        var skillReattach = options.SkillReattachContentProvider;
        return async (history, sink, ct) =>
        {
                var didCompact = await this.compactHistoryAsync(client, options.Model, "auto", sink, ct).ConfigureAwait(false);
                // PostCompact additionalContext is injected inside compactHistoryAsync (before we
                // return here), so skill re-attach goes after it — skill bodies are closest to the
                // model's next turn.
                if (didCompact && skillReattach is not null)
                {
                    var content = skillReattach(options.AutoCompactTokenThreshold);
                    if (!string.IsNullOrEmpty(content))
                    {
                        // Skip if adding reattach would bring history back up to the threshold,
                        // which would trigger compaction again on the next iteration.
                        var postCompactTokens = TokenEstimator.Estimate(history);
                        var reattachTokenEstimate = content.Length / 4;
                        var wouldExceedThreshold = options.AutoCompactTokenThreshold > 0
                            && postCompactTokens + reattachTokenEstimate >= options.AutoCompactTokenThreshold;

                        // Skip if reattach is already the trailing message (exactly-once guard).
                        var alreadyLastMessage = history.Count > 0
                            && history[^1].Role == ChatRole.User
                            && history[^1].Content is [TextBlock tbLast]
                            && tbLast.Text == content;

                        if (!wouldExceedThreshold && !alreadyLastMessage)
                        {
                            history.Add(new ChatMessage(ChatRole.User, [new TextBlock(content)]));
                        }
                    }
                }

                return didCompact;
        };
    }

    /// <summary>
    /// Assembles the <see cref="AgentLoopSpec"/> for one ISOLATED scheduled firing.
    /// root runs an independent conversation that never touches the session's main history, yet
    /// reuses the CURRENT session's provider/model/effort/output style (via <paramref name="options"/>),
    /// the same live <see cref="BuildPermissions"/> path (including the shared
    /// <see cref="PermissionModeState"/>, classifier, and rules), the shared task manager, LSP,
    /// user hooks, and prompt services.
    /// </summary>
    /// <remarks>
    /// Deliberately diverges from <see cref="BuildSpec"/> for isolation:
    /// <list type="bullet">
    ///   <item>No todos/schedules/schedule-runtime, no goal supervisor or in-loop compaction, no
    ///     incremental persistence or execution gate — the isolated history is transient.</item>
    ///   <item>No SessionMemory post-sampling hook bus; user-configured hooks (from settings) still
    ///     apply to tool executions so behavior stays consistent.</item>
    ///   <item>Every <c>schedule_*</c> tool is removed from BOTH the scheduled root's registry and
    ///     the child subagent host's registry, so a scheduled agent can neither create nor manage
    ///     schedules (and a depth-2 child cannot reintroduce them). Task lifecycle tools are kept so
    ///     the scheduled agent can inspect/manage only its authorized descendants.</item>
    ///   <item><see cref="AgentLoopSpec.CurrentTaskId"/>/<see cref="AgentLoopSpec.CurrentDepth"/> are
    ///     set from the caller so the tool context carries the scheduled root's trusted identity.</item>
    /// </list>
    /// Steering is intentionally NOT set here — the host applies the task's steering inbox via
    /// <c>with { Steering = ... }</c>.
    /// </remarks>
    /// <param name="options">The current session options snapshot for this firing.</param>
    /// <param name="client">The per-execution provider chat client for this firing.</param>
    /// <param name="settings">The settings loaded by the caller (never re-loaded here).</param>
    /// <param name="taskId">The scheduled root task's id (from the task manager).</param>
    /// <param name="depth">The scheduled root task's depth (1 for a scheduled root).</param>
    public AgentLoopSpec BuildScheduledSpec(SessionOptions options, ILlmClient client, CodaSettings settings, string taskId, int depth)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(taskId);

        var includeAnthropicSystemPrefix = options.ProviderId != GitHubCopilotProvider.Id;

        var agentOptions = this.BuildAgentOptions(options);

        var (permissions, permissionRules) = BuildPermissions(options, client, settings);

        var hookContext = new HookContext(
            SessionId: this.tasks.SessionId,
            Cwd: agentOptions.WorkingDirectory);
        var hookList = (IReadOnlyList<UserHook>?)this.sessionHookList ?? settings.Hooks;
        var (httpHandler, promptHandler, agentHandler) = hookList.Count > 0
            ? this.BuildHookHandlers(client, settings, options, agentOptions, permissions, includeAnthropicSystemPrefix)
            : default;
        var userHooks = hookList.Count > 0
            ? new UserHookRunner(hookList, context: hookContext,
                logger: this.loggerFactory.CreateLogger("Coda.Hooks"),
                httpHandler: httpHandler, promptHandler: promptHandler, agentHandler: agentHandler,
                trustGuard: this.trustGuard, runLog: this.runLog)
            : null;

        // A normal child host so the scheduled root (depth 1) can create depth-2 children; depth-3
        // is rejected by the child host (depth >= MaxSubagentDepth). Built with schedule_* tools
        // stripped so a depth-2 child cannot reintroduce them.
        var subagentTools = StripSkillTool(StripScheduleTools([.. BuiltInTools.All(), .. options.ExtraTools]).All);
        var subagentHost = new SubagentHost(client, subagentTools, permissions, agentOptions, this.tasks, includeAnthropicSystemPrefix, userHooks);

        var tools = this.BuildScheduledTools(options);

        return new AgentLoopSpec(
            client,
            tools,
            permissions,
            agentOptions,
            subagentHost,
            // No SessionMemory watcher for the isolated scheduled history.
            Hooks: null,
            // Isolated: no session todo/schedule stores, no schedule runtime.
            Todos: null,
            Schedules: null,
            UserQuestion: options.UserQuestionPrompt,
            UserHooks: userHooks,
            PlanApprover: options.PlanApprover,
            Lsp: this.lspManager,
            LspDiagnostics: this.lspDiagnostics,
            ToolSearch: this.toolSearchCoordinator,
            // No goal supervisor / in-loop compaction for the isolated run.
            Goal: null,
            CompactAsync: null,
            Logger: this.loggerFactory.CreateLogger("Coda.Tool"),
            // Steering is applied by the host from the task's inbox.
            Steering: null,
            // No incremental persistence for the transient scheduled history.
            PersistTurnAsync: null,
            // Share the task manager so descendants are visible/authorized.
            Tasks: this.tasks,
            Gate: null,
            ScheduleRuntime: null,
            CurrentTaskId: taskId,
            CurrentDepth: depth)
        {
            PermissionRules = permissionRules,
            GrantedDirectoriesSource = options.GrantedDirectoriesSource,
        };
    }

    /// <summary>
    /// Builds the scheduled root's tool registry: the same built-ins + extra tools + TaskTool +
    /// LSP/tool-search (when configured) as a main turn, but with every <c>schedule_*</c> tool
    /// removed so a scheduled agent cannot create or manage schedules.
    /// </summary>
    private ToolRegistry BuildScheduledTools(SessionOptions options)
    {
        var extraLspTools = this.lspManager is not null
            ? new ITool[] { new TaskTool(), new LspTool() }
            : new ITool[] { new TaskTool() };

        var toolSearchTools = this.toolSearchCoordinator is not null
            ? new ITool[] { new ToolSearchTool() }
            : [];

        return StripScheduleTools([.. BuiltInTools.All(), .. options.ExtraTools, .. extraLspTools, .. toolSearchTools]);
    }

    /// <summary>Returns a registry with every <c>schedule_*</c> tool removed.</summary>
    private static ToolRegistry StripScheduleTools(IEnumerable<ITool> tools) =>
        new(tools.Where(t => !t.Name.StartsWith("schedule_", StringComparison.Ordinal)));

    /// <summary>Returns a registry with the <c>skill</c> tool removed.</summary>
    /// <remarks>
    /// Subagents must not receive the <c>skill</c> tool because it shares mutable
    /// <c>SkillSessionState</c> with the root session. A subagent invocation would permanently
    /// mark a skill loaded in the root's state without adding the body to the root's history;
    /// the reattach provider is only wired for the root, so re-attachment after compaction would
    /// then inject bodies the root never actually loaded — the model would believe it has
    /// instructions it does not have. Skills are a session-level capability of the main agent.
    /// </remarks>
    private static ToolRegistry StripSkillTool(IEnumerable<ITool> tools) =>
        new(tools.Where(t => t.Name != "skill"));

    /// <summary>Builds the agent options: effective root system prompt + base bounds.</summary>
    private AgentOptions BuildAgentOptions(SessionOptions options)
    {
        return new AgentOptions
        {
            Model = options.Model,
            SystemPrompt = EffectiveSystemPrompt.Resolve(options),
            WorkingDirectory = options.WorkingDirectory,
            PermissionMode = options.PermissionMode,
            PermissionModeState = options.PermissionModeState,
            MaxIterations = options.MaxIterations,
            // Resolve max_tokens from the model's REAL published output ceiling (catalog), clamping any
            // explicit override to it — a flat default would 400 a smaller-cap model (e.g. Copilot's
            // claude-sonnet-4 at 16000) and truncate a larger one.
            MaxTokens = ModelLimits.ResolveMaxOutputTokens(ModelCatalog.Default, options.ProviderId, options.Model, options.MaxTokens),
            MaxStopContinuations = options.MaxStopContinuations,
            Effort = options.Effort,
        };
    }

    /// <summary>
    /// Builds the permission policy: the mode/classifier base, then a rules wrapper when the
    /// settings carry any allow/deny rules.
    /// </summary>
    private static (IPermissionPrompt Permissions, PermissionRuleStore Rules) BuildPermissions(
        SessionOptions options,
        ILlmClient client,
        CodaSettings settings)
    {
        // Read the mode live from the shared session state when supplied, so a mid-run mode change
        // is applied to the next decision; otherwise wrap a fixed state from the snapshot.
        var state = options.PermissionModeState ?? new PermissionModeState(options.PermissionMode);

        // When the bypass classifier is enabled, build a mode-aware prompt that consults the safety
        // classifier only while the live mode is Bypass (escalating risky actions) and otherwise
        // applies the standard mode policy. Building it regardless of the snapshot mode keeps a live
        // Default→Bypass switch classifier-gated and a live Bypass→Default switch back to asking.
        IPermissionPrompt permissions;
        if (options.EnableBypassClassifier)
        {
            var classifier = new LlmToolActionClassifier(new ForkedAgentRunner(client, options.Model));
            permissions = new LiveBypassClassifierPermissionPrompt(state, classifier, options.InteractivePrompt);
        }
        else
        {
            permissions = new ModePermissionPrompt(state, options.InteractivePrompt);
        }

        // Always wrap the base permissions with the live rule store. The store starts empty
        // when no rules are pre-configured; rules added mid-session by a PermissionRequest hook
        // take effect immediately because RulesPermissionPrompt reads the same store instance.
        var ruleStore = new PermissionRuleStore(
            settings.Allow.Select(PermissionRule.Parse),
            settings.Deny.Select(PermissionRule.Parse));

        permissions = new RulesPermissionPrompt(ruleStore, permissions);

        return (permissions, ruleStore);
    }

    /// <summary>
    /// Builds the goal supervisor when a goal is active and returns the (possibly mutated) agent
    /// options. With no goal, returns the supervisor as null and the options unchanged.
    /// GoalDefaults resolves the run budget from per-run overrides, project/user settings, and
    /// built-in defaults (24 h / 60 000 turns).
    /// </summary>
    private static (GoalSupervisor? Goal, AgentOptions Options) BuildGoalSupervisor(
        SessionOptions options,
        ILlmClient client,
        CodaSettings settings,
        AgentOptions agentOptions)
    {
        if (string.IsNullOrWhiteSpace(options.Goal))
        {
            return (null, agentOptions);
        }

        var goalDefaults = GoalDefaults.Resolve(settings.Goal, options.GoalMaxDuration, options.GoalMaxContinuations);
        var budget = GoalBudget.StartNow(goalDefaults.MaxDuration, goalDefaults.MaxContinuations, goalDefaults.ExtensionFraction);
        var goalSupervisor = new GoalSupervisor(new ForkedAgentRunner(client, options.Model), options.Goal!, budget);
        var updatedOptions = agentOptions with
        {
            AutoCompact = goalDefaults.AutoCompact,
            AutoCompactTokenThreshold = options.AutoCompactTokenThreshold,
        };

        return (goalSupervisor, updatedOptions);
    }

    /// <summary>Builds the leader's subagent host, sharing the turn's client, permissions, agent options and user hooks.</summary>
    private static SubagentHost BuildSubagentHost(
        SessionOptions options,
        ILlmClient client,
        AgentOptions agentOptions,
        IPermissionPrompt permissions,
        bool includeAnthropicSystemPrefix,
        UserHookRunner? userHooks,
        TaskManager tasks)
    {
        var subagentTools = StripSkillTool([.. BuiltInTools.All(), .. options.ExtraTools]);
        return new SubagentHost(client, subagentTools, permissions, agentOptions, tasks, includeAnthropicSystemPrefix, userHooks);
    }

    /// <summary>
    /// Builds the three handler instances (<c>http</c>, <c>prompt</c>, <c>agent</c>) used by
    /// the session's <see cref="UserHookRunner"/>. Each handler is bound to the current
    /// turn's resolved client and settings.
    /// </summary>
    /// <remarks>
    /// Security invariants enforced here by construction:
    /// <list type="bullet">
    ///   <item>The <c>http</c> handler owns a non-redirecting <see cref="System.Net.Http.HttpClient"/>
    ///     (passed as <see langword="null"/> so <see cref="HttpHookHandler"/> creates the safe default).
    ///     </item>
    ///   <item>The <c>agent</c> handler's <see cref="SubagentHost"/> is constructed with
    ///     <c>userHooks: null</c> so hook-spawned subagents are structurally hook-free and
    ///     recursive hook firing is impossible by construction, not by assertion.</item>
    /// </list>
    /// </remarks>
    private (IHookHandler http, IHookHandler prompt, IHookHandler agent) BuildHookHandlers(
        ILlmClient client,
        CodaSettings settings,
        SessionOptions options,
        AgentOptions agentOptions,
        IPermissionPrompt permissions,
        bool includeAnthropicSystemPrefix)
    {
        var httpHandler = new HttpHookHandler(
            httpClient: null, // non-redirecting default
            settings.HttpHookAllowlist,
            logger: this.loggerFactory.CreateLogger("Coda.Hooks.Http"));

        var promptHandler = new PromptHookHandler(
            new ForkedAgentRunner(client, options.Model),
            logger: this.loggerFactory.CreateLogger("Coda.Hooks.Prompt"));

        // Hook-free by construction: userHooks: null prevents recursive hook firing inside
        // hook-spawned subagents.
        var hookFreeHost = BuildSubagentHost(
            options, client, agentOptions, permissions, includeAnthropicSystemPrefix,
            userHooks: null, this.tasks);
        var agentHandler = new AgentHookHandler(
            hookFreeHost,
            this.loggerFactory.CreateLogger("Coda.Hooks.Agent"));

        return (httpHandler, promptHandler, agentHandler);
    }

    /// <summary>
    /// Builds the parent (leader) tool registry: the built-ins + extra tools, plus the LSP
    /// and tool-search tools gated on whether their backing collaborators are configured.
    /// </summary>
    private ToolRegistry BuildParentTools(SessionOptions options)
    {
        // Include LspTool only when an LSP manager is configured. This ensures the
        // model only sees the tool when language servers are actually available.
        var extraLspTools = this.lspManager is not null
            ? new ITool[] { new TaskTool(), new LspTool() }
            : new ITool[] { new TaskTool() };

        // Register ToolSearchTool only when tool search is active; in Standard mode it
        // is unnecessary and would appear as a confusing extra tool in the inline list.
        var toolSearchTools = this.toolSearchCoordinator is not null
            ? new ITool[] { new ToolSearchTool() }
            : [];

        return new ToolRegistry([.. BuiltInTools.All(), .. options.ExtraTools, .. extraLspTools, .. toolSearchTools]);
    }

    /// <summary>Builds the watcher/stop-hook bus from the opt-in options, or null when none are enabled.</summary>
    private static AgentHooks? BuildHooks(ILlmClient client, SessionOptions options)
    {
        var postSampling = new List<IPostSamplingHook>();
        var stopHooks = new List<IStopHook>();

        if (options.EnableSessionMemory)
        {
            var fork = new ForkedAgentRunner(client, options.Model);
            postSampling.Add(new SessionMemoryWatcher(fork, new FileSessionMemoryStore(options.WorkingDirectory)));
        }

        // Goals are now handled by GoalSupervisor passed directly to AgentLoop — not via IStopHook.

        if (postSampling.Count == 0 && stopHooks.Count == 0)
        {
            return null;
        }

        return new AgentHooks(postSampling, stopHooks);
    }
}
