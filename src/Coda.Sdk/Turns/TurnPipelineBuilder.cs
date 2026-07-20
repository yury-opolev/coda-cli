using Coda.Agent;
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Classifier;
using Coda.Agent.Goals;
using Coda.Agent.Hooks;
using Coda.Agent.Lsp;
using Coda.Agent.OutputStyles;
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
    private readonly BackgroundTaskRunner backgroundTasks;
    private readonly LspServerManager? lspManager;
    private readonly LspDiagnosticRegistry? lspDiagnostics;
    private readonly ToolSearchCoordinator? toolSearchCoordinator;
    private readonly ILoggerFactory loggerFactory;
    private readonly Func<ILlmClient, string, CancellationToken, Task> compactHistoryAsync;

    /// <summary>
    /// Creates the builder with the session's stable per-session collaborators. These do not
    /// change between turns, so the builder is constructed once in the session ctor.
    /// </summary>
    /// <param name="todos">Shared todo store across the session.</param>
    /// <param name="schedules">Scheduled-task store backing the schedule tools.</param>
    /// <param name="backgroundTasks">Runner for background (detached) tasks.</param>
    /// <param name="lspManager">Language-server manager, or null when no LSP servers are configured.</param>
    /// <param name="lspDiagnostics">Diagnostics registry paired with <paramref name="lspManager"/>, or null.</param>
    /// <param name="toolSearchCoordinator">Coordinator backing the tool-search tool, or null in Standard mode.</param>
    /// <param name="loggerFactory">Factory for the loop's tool/turn loggers.</param>
    /// <param name="compactHistoryAsync">
    /// Compaction delegate bound to the session's in-place history compaction
    /// (<c>CodaSession.CompactHistoryAsync</c>); invoked by the goal-run compact callback.
    /// </param>
    public TurnPipelineBuilder(
        TodoStore todos,
        ScheduledTaskStore schedules,
        BackgroundTaskRunner backgroundTasks,
        LspServerManager? lspManager,
        LspDiagnosticRegistry? lspDiagnostics,
        ToolSearchCoordinator? toolSearchCoordinator,
        ILoggerFactory loggerFactory,
        Func<ILlmClient, string, CancellationToken, Task> compactHistoryAsync)
    {
        this.todos = todos ?? throw new ArgumentNullException(nameof(todos));
        this.schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        this.backgroundTasks = backgroundTasks ?? throw new ArgumentNullException(nameof(backgroundTasks));
        this.lspManager = lspManager;
        this.lspDiagnostics = lspDiagnostics;
        this.toolSearchCoordinator = toolSearchCoordinator;
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        this.compactHistoryAsync = compactHistoryAsync ?? throw new ArgumentNullException(nameof(compactHistoryAsync));
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

        var agentOptions = this.BuildAgentOptions(options, includeAnthropicSystemPrefix);

        var permissions = BuildPermissions(options, client, settings);

        // The goal step may mutate agentOptions (AutoCompact + threshold) when a goal is active.
        var (goalSupervisor, goalAgentOptions) = BuildGoalSupervisor(options, client, settings, agentOptions);
        agentOptions = goalAgentOptions;

        // Note: SessionMemory post-sampling hook writes notes in background; if the turn is later
        // rolled back on error, the notes file may still reflect the rolled-back turn — this is
        // acceptable because the notes file is advisory and idempotent.
        var hooks = BuildHooks(client, options);

        var userHooks = settings.Hooks.Count > 0 ? new UserHookRunner(settings.Hooks) : null;

        var subagentHost = BuildSubagentHost(options, client, agentOptions, permissions, includeAnthropicSystemPrefix, userHooks);

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
            BackgroundTasks: this.backgroundTasks,
            Lsp: this.lspManager,
            LspDiagnostics: this.lspDiagnostics,
            ToolSearch: this.toolSearchCoordinator,
            Goal: goalSupervisor,
            // The loop runs on the session history, which the compaction delegate compacts in
            // place, so the list argument is intentionally ignored.
            CompactAsync: goalSupervisor is null ? null : (_, ct) => this.compactHistoryAsync(client, options.Model, ct),
            Logger: this.loggerFactory.CreateLogger("Coda.Tool"));
    }

    /// <summary>Builds the agent options: system prompt (with/without the anthropic prefix) + output style + base bounds.</summary>
    private AgentOptions BuildAgentOptions(SessionOptions options, bool includeAnthropicSystemPrefix)
    {
        var outputStyle = BuiltInOutputStyles.Resolve(options.OutputStyle);
        return new AgentOptions
        {
            Model = options.Model,
            SystemPrompt = AgentSystemPrompt.Build(
                options.WorkingDirectory,
                includeAnthropicSystemPrefix,
                ProjectContext.Load(options.WorkingDirectory),
                outputStyle.SystemPromptSuffix),
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
    private static IPermissionPrompt BuildPermissions(SessionOptions options, ILlmClient client, CodaSettings settings)
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

        // Wrap the base permissions with the allow/deny rule lists when any rules exist.
        if (settings.Allow.Count > 0 || settings.Deny.Count > 0)
        {
            var allowRules = settings.Allow.Select(PermissionRule.Parse).ToList();
            var denyRules = settings.Deny.Select(PermissionRule.Parse).ToList();
            permissions = new RulesPermissionPrompt(allowRules, denyRules, permissions);
        }

        return permissions;
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
        UserHookRunner? userHooks)
    {
        var subagentTools = new ToolRegistry([.. BuiltInTools.All(), .. options.ExtraTools]);
        return new SubagentHost(client, subagentTools, permissions, agentOptions, includeAnthropicSystemPrefix, userHooks);
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
