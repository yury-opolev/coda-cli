using Coda.Agent;
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Tasks;
using Coda.Agent.Compaction;
using Coda.Agent.Goals;
using Coda.Agent.Hooks;
using Coda.Agent.Lsp;
using Coda.Agent.Scheduling;
using Coda.Agent.Settings;
using Coda.Agent.ToolSearch;
using Coda.Agent.Tools;
using Coda.Agent.Watchers;
using Coda.Common;
using Coda.Sdk.Telemetry;
using Coda.Sdk.Turns;
using LlmAuth;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Coda.Sdk;

/// <summary>
/// The callable Coda engine: wires the provider client + tools + subagents +
/// permission policy and runs the agent loop, keeping the conversation across
/// calls. Used by the TUI, the headless CLI, and in-process side-agents alike.
/// </summary>
public sealed partial class CodaSession : IDisposable, IAsyncDisposable
{
    /// <summary>Bounded timeout for graceful teardown of the LSP servers on dispose.</summary>
    internal static readonly TimeSpan LspDisposeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Budget for the synchronous <see cref="Dispose"/>, which drives the whole async teardown on a
    /// worker thread. It sums the TaskManager shutdown budget (running work + shell tree-kills) and
    /// <see cref="LspDisposeTimeout"/> so schedule-runtime/HTTP/logger/LSP disposal completes before
    /// the sync call returns — bounded (never unbounded), yet large enough not to sever a
    /// still-progressing teardown at the shorter LSP-only timeout. The schedule runtime is disposed
    /// first and only cancels its loop (it never waits on running scheduled tasks — the TaskManager
    /// owns those), so it adds no measurable time and needs no separate budget line.
    /// </summary>
    internal static readonly TimeSpan SyncDisposeBudget =
        TaskManager.DefaultShutdownBudget + LspDisposeTimeout;

    private readonly CredentialManager credentials;
    private readonly ClientFingerprint fingerprint;
    private readonly ILlmClientFactory llmClientFactory;
    private readonly IAgentLoopFactory agentLoopFactory;
    private readonly HttpClient http;
    private readonly HttpClient? ownedHttpClient;
    private readonly List<ChatMessage> history;
    private readonly TodoStore todos = new();
    private readonly ScheduledTaskStore schedules;
    private readonly TaskManager tasks;
    private readonly Coda.Agent.Settings.SubagentSettings subagentSettings;
    private readonly LspServerManager? lspManager;
    private readonly LspDiagnosticRegistry? lspDiagnostics;
    private readonly ToolSearchCoordinator? toolSearchCoordinator;
    private readonly string? startupSystemPromptOverride;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly TurnPipelineBuilder turnPipelineBuilder;
    private readonly SteeringInbox steeringInbox = new();

    /// <summary>
    /// Resolved allow/deny filter for the main agent's tool registry.
    /// Null = no filter. Applied per turn in <c>ResolveEffectiveOptions</c> so that
    /// <see cref="Turns.TurnPipelineBuilder.BuildParentTools"/> picks it up without
    /// needing a separate constructor parameter on the builder.
    /// </summary>
    private readonly Coda.Agent.Tools.ToolNameFilter? agentToolFilter;
    /// <summary>
    /// Test seam: when non-null, overrides the <see cref="UserHookRunner"/> produced by
    /// <see cref="Turns.TurnPipelineBuilder"/> so unit tests can inject controlled hook behaviour
    /// without spawning real processes or writing settings to disk. Null in production.
    /// </summary>
    private readonly UserHookRunner? userHookRunnerOverride;
    private readonly List<UserHook> configuredHooks;
    private readonly HookRunLog hookRunLog;
    private readonly HookTrustGuard? trustGuard;
    /// <summary>
    /// Test seam: executor override forwarded to <see cref="sessionHookRunner"/> at construction.
    /// Null in production (real <see cref="ShellHookExecutor"/> is used).
    /// </summary>
    private readonly Func<string, string, CancellationToken, Task<(int, string)>>? sessionExecOverride;
    /// <summary>
    /// Test seam: prompt handler override forwarded to <see cref="sessionHookRunner"/> at construction.
    /// Null in production (real handler is wired in <see cref="RebuildSessionRunnerWithHandlers"/>).
    /// </summary>
    private readonly IHookHandler? sessionPromptHandlerOverride;
    /// <summary>
    /// Set to <see langword="true"/> once <see cref="RebuildSessionRunnerWithHandlers"/> has been
    /// called so the upgrade runs at most once per session lifetime.
    /// </summary>
    private bool sessionRunnerHandlersUpgraded
    {
        get => this.sessionHooks.HandlersUpgraded;
        set => this.sessionHooks.HandlersUpgraded = value;
    }

    private TokenUsage sessionUsage = TokenUsage.Zero;
    private GoalStatus? lastGoalStatus;

    // Prompt-cache hygiene tracking (Phase 2/3).
    private string? lastResolvedSystemPrompt;
    private int turnsWithZeroCacheActivity;
    private bool cacheZeroActivityWarned;

    /// <summary>
    /// Number of consecutive turns with zero cache activity before a one-time warning is emitted.
    /// Three turns is enough to rule out a first-turn cold-start write without false positives.
    /// </summary>
    public const int ZeroActivityWarnAfterTurns = 3;

    /// <summary>
    /// The session-level hook concern: the session hook runner, the SessionStart / SessionEnd /
    /// Notification firings, and the session-scoped application of the SessionStart outputs.
    /// </summary>
    private readonly SessionLifecycleHooks sessionHooks;

    /// <summary>Shorthand for the session hook runner owned by <see cref="sessionHooks"/>.</summary>
    private UserHookRunner? sessionHookRunner
    {
        get => this.sessionHooks.Runner;
        set => this.sessionHooks.Runner = value;
    }

    // Compaction hook runner (Phase 5). Set to loopSpec.UserHooks before each RunAsync turn so
    // CompactHistoryAsync (called both pre-turn and by TurnPipelineBuilder's in-loop delegate)
    // can fire Pre/PostCompact hooks. Shared single field; no race because RunAsync is not
    // re-entrant (one user turn at a time).
    private UserHookRunner? compactionHooks;

    // Reused across the incremental "record on the go" saves so the store's createdUtc cache
    // survives between turns (a fresh store per call would re-read the file every save).
    private SessionTranscriptStore? transcriptStore;

    private SessionAuditStore? auditStore;
    private int auditTurnIndex;
    private string? auditCounterForId;

    /// <summary>
    /// Optional stream-progress sink injected by the serve layer (the Bridge liveness
    /// pulse). Null in standalone/TUI runs — the client falls back to telemetry-log
    /// progress only. Set before a turn runs; picked up at per-turn client construction.
    /// </summary>
    public IStreamProgressSink? StreamProgressSink { get; set; }

    public CodaSession(
        CredentialManager credentials,
        SessionOptions options,
        ClientFingerprint? fingerprint = null,
        HttpClient? httpClient = null,
        List<ChatMessage>? history = null,
        string? sessionId = null,
        ILlmClientFactory? llmClientFactory = null,
        IAgentLoopFactory? agentLoopFactory = null,
        Func<SessionOptions>? currentOptionsProvider = null,
        TimeProvider? timeProvider = null,
        UserHookRunner? userHookRunnerOverride = null,
        HookTrustGuard? trustGuard = null,
        HookRunLog? runLog = null,
        IReadOnlyList<UserHook>? hookList = null,
        Coda.Agent.Subagents.SubagentRegistry? subagentRegistry = null,
        Func<string, string, CancellationToken, Task<(int, string)>>? sessionExecOverride = null,
        Coda.Agent.Hooks.IHookHandler? sessionPromptHandlerOverride = null,
        ToolSearchCoordinator? toolSearchCoordinatorOverride = null)
    {
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.startupSystemPromptOverride = options.SystemPromptOverride;
        this.fingerprint = fingerprint ?? new ClientFingerprint();
        this.llmClientFactory = llmClientFactory ?? new DefaultLlmClientFactory();
        this.agentLoopFactory = agentLoopFactory ?? new DefaultAgentLoopFactory();
        // Live options accessor for scheduled firings. Defaults to the current volatile Options (not a
        // construction snapshot), so a mid-session model/effort/tool/permission change is picked up.
        this.currentOptionsProvider = currentOptionsProvider ?? (() => this.Options);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.userHookRunnerOverride = userHookRunnerOverride;
        this.history = history ?? [];
        this.SessionId = sessionId ?? SessionIds.NewId();
        this.trustGuard = trustGuard;
        this.hookRunLog = runLog ?? new HookRunLog();
        this.sessionExecOverride = sessionExecOverride;
        this.sessionPromptHandlerOverride = sessionPromptHandlerOverride;
        this.sessionHooks = new SessionLifecycleHooks(this.SessionId);
        if (options.SessionSource is { } src)
        {
            this.sessionHooks.Source = src;
        }
        // Loaded before the task manager because the manager's subagent limits come from it.
        // Resolved once here rather than in each host (TUI/headless/serve): every host reaches the
        // session through this constructor, so a host that forgets to pass the block still gets the
        // configured limits instead of silently falling back to the defaults.
        var settings = SettingsLoader.Load(options.WorkingDirectory);
        this.subagentSettings = options.SubagentSettings ?? settings.Subagents;

        // Resolve agent.tools filter from settings; options can override for testing.
        this.agentToolFilter = options.AgentToolFilter ?? settings.AgentToolFilter;

        // The manager groups persistent task logs under the session id captured HERE. If the id
        // is later adopted (AdoptSessionId/Resume), the manager keeps this original grouping so
        // active task logs are never moved out from under open writers — see AdoptSessionId.
        this.tasks = new TaskManager(this.SessionId, subagentSettings: this.subagentSettings);
        if (httpClient is null)
        {
            // No HttpClient.Timeout: it would cap the TOTAL stream duration and kill a
            // long-but-healthy response. A hung call is bounded inside the LLM client by
            // its response-headers / stream-idle guards (LlmHttpTimeoutConfig).
            this.ownedHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            this.http = this.ownedHttpClient;
        }
        else
        {
            this.http = httpClient;
        }

        var schedulesPath = Path.Combine(options.WorkingDirectory, ".coda", "scheduled_tasks.json");
        this.schedules = new ScheduledTaskStore(schedulesPath);

        // Load LSP servers from settings (loaded above) and merge with any plugin-contributed
        // servers. Plugin keys are namespaced (plugin:<name>:<server>) so clashes with settings keys
        // are rare; settings always win on exact-key clashes.
        // Use caller-provided hook list when available (supports /hooks enable/disable and
        // the shared run log). Preserve the mutable-list contract: when the caller already
        // gave us a List<UserHook>, reuse it so HookManagementService.SetEnabled mutations
        // are visible inside this session. Otherwise copy. Falls back to settings hooks.
        this.configuredHooks = (hookList as List<UserHook>)
            ?? (hookList is not null ? new List<UserHook>(hookList) : new List<UserHook>(settings.Hooks));

        // Plugin LSP servers arrive already filtered to the plugins the user enabled and approved for
        // the Lsp class; scanning for them here would bypass that gate, and starting one runs a process.
        var lspServers = LspServerMapBuilder.Build(
            settings.LspServers,
            options.PluginLspServers);

        if (lspServers.Count > 0)
        {
            this.lspManager = new LspServerManager(
                lspServers,
                (name, cfg) => new LspServerInstance(name, cfg,
                    new LspClient(name, async ct => (ILspTransport)await ProcessLspTransport.StartAsync(
                        cfg.Command,
                        cfg.Args,
                        cfg.Env,
                        options.WorkingDirectory,
                        name,
                        ct).ConfigureAwait(false)),
                    workspaceRoot: options.WorkingDirectory));

            this.lspDiagnostics = new LspDiagnosticRegistry();
        }

        // Build the tool-search coordinator from the ENABLE_TOOL_SEARCH environment variable.
        // Only store (and later pass to the leader loop) when mode is active; Standard mode
        // keeps the coordinator null so the agent loop behaves byte-identically to before.
        // For TstAuto, we pass the parsed auto-percentage and a fixed 200 000-token context
        // window budget (the default Claude context window; no token-count API available here).
        var toolSearchEnv = Environment.GetEnvironmentVariable("ENABLE_TOOL_SEARCH");
        var toolSearchMode = ToolSearchModeResolver.Resolve(toolSearchEnv);
        if (toolSearchMode != ToolSearchMode.Standard)
        {
            var autoPercent = ToolSearchModeResolver.ResolveAutoPercentage(toolSearchEnv);
            this.toolSearchCoordinator = new ToolSearchCoordinator(toolSearchMode, autoPercent, contextWindowTokens: ContextWindow.DefaultTokens);
        }

        // Test seam: a caller-supplied coordinator wins over the env-var-derived one so unit tests
        // can exercise deferral behaviour without mutating process-wide environment variables.
        if (toolSearchCoordinatorOverride is not null)
        {
            this.toolSearchCoordinator = toolSearchCoordinatorOverride;
        }

        // Built last so that if any wiring above throws, no telemetry file handle is
        // opened and then leaked (the session is never returned, so Dispose never runs).
        // A per-session TelemetryOverride (e.g. `coda serve --telemetry`) wins over the
        // settings-file block; environment overrides still layer on top via Resolve.
        var loggerSetup = CodaLoggerFactory.Create(
            TelemetryResolver.Resolve(this.options.TelemetryOverride ?? settings.Telemetry));
        this.loggerFactory = loggerSetup.Factory;
        this.LogFilePath = loggerSetup.LogFilePath;
        this.logger = this.loggerFactory.CreateLogger("Coda.Session");
        this.sessionHooks.Logger = this.loggerFactory.CreateLogger("Coda.Session.Hooks");

        // The schedules store was constructed above, before the logger factory existed (telemetry
        // is built last to avoid leaking a file handle on a wiring failure). Wire its logger now so
        // best-effort persistence failures are actually surfaced in production, not just in tests.
        this.schedules.Logger = this.loggerFactory.CreateLogger("Coda.Schedules");

        // Built once with the session's stable collaborators. Each turn's per-turn assembly is
        // delegated to this builder (see RunAsync); only the per-turn inputs vary.
        this.turnPipelineBuilder = new TurnPipelineBuilder(
            this.todos,
            this.schedules,
            this.tasks,
            this.lspManager,
            this.lspDiagnostics,
            this.toolSearchCoordinator,
            this.loggerFactory,
            // Wrap the 5-param method into the expected delegate signature.
            (client, model, trigger, sink, ct) => this.CompactHistoryAsync(client, model, trigger, sink, ct),
            // Evaluated per turn, so once InitializeAsync starts the runtime the main schedule_list
            // sees the live view; it returns null before initialization and when scheduling is off.
            () => this.scheduleRuntime,
            sessionHookList: this.configuredHooks,
            runLog: this.hookRunLog,
            trustGuard: this.trustGuard,
            subagentRegistry: subagentRegistry);

        this.logger.LogInformation(
            "Session {sessionId} started: provider {provider}, model {model}",
            this.SessionId, options.ProviderId, options.Model);

        // Session-level hook runner for SessionStart / SessionEnd / Notification / Pre/PostCompact.
        // Built for any configured hook so that compaction hooks (not session-lifecycle events)
        // are also wired here — the old `hasSessionLevelHooks` guard was the IMPORTANT-1 bug.
        // In tests, userHookRunnerOverride serves as both the per-turn and session-level runner;
        // sessionExecOverride / sessionPromptHandlerOverride are minimal seams that inject only
        // the executor or prompt handler without replacing the whole runner.
        if (userHookRunnerOverride is not null)
        {
            this.sessionHookRunner = userHookRunnerOverride;
            this.sessionRunnerHandlersUpgraded = true; // override already has whatever it needs
        }
        else if (this.configuredHooks.Count > 0)
        {
            // Build the session runner immediately with the available handlers.
            // Prompt/agent handlers need an LLM client and are added later via
            // RebuildSessionRunnerWithHandlers (called from RunAsync and CompactAsync).
            // sessionPromptHandlerOverride lets tests inject a fake handler right now
            // without needing the upgrade path.
            var sessionHttpHandler = new Coda.Agent.Hooks.HttpHookHandler(
                httpClient: null,
                settings.HttpHookAllowlist,
                logger: this.loggerFactory.CreateLogger("Coda.Hooks.Http"));
            this.sessionHookRunner = new UserHookRunner(
                this.configuredHooks,
                execOverride: this.sessionExecOverride,
                context: new HookContext(this.SessionId, options.WorkingDirectory),
                logger: this.loggerFactory.CreateLogger("Coda.Hooks.Session"),
                httpHandler: sessionHttpHandler,
                promptHandler: this.sessionPromptHandlerOverride,
                trustGuard: this.trustGuard,
                runLog: this.hookRunLog);
            // Mark upgraded only when the test seam already provided the prompt handler.
            this.sessionRunnerHandlersUpgraded = this.sessionPromptHandlerOverride is not null;
        }

        // Wire task-complete notifications for background tasks. Scoped to the session lifetime
        // so a late completion cannot spawn a hook subprocess after teardown has begun.
        if (this.sessionHookRunner?.HasNotification == true)
        {
            this.tasks.NotificationCallback = this.sessionHooks.RunTaskNotificationAsync;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "transcript persistence failed (best-effort); the turn is unaffected: session={sessionId}")]
    private partial void LogTranscriptPersistFailed(string sessionId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "audit persistence failed for session {sessionId} (best-effort; the turn is unaffected)")]
    private partial void LogAuditPersistFailed(string sessionId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LSP shutdown failed (best-effort) during session teardown: session={sessionId}")]
    private partial void LogLspShutdownFailed(string sessionId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "turn failed: provider={providerId} model={model} {errorType}: {errorMessage}")]
    private partial void LogTurnFailed(string providerId, string model, string errorType, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "synchronous dispose worker failed (best-effort): session={sessionId}")]
    private partial void LogSyncDisposeFailed(string sessionId, Exception ex);

    /// <summary>Stable identifier for this session, used to persist/resume conversation transcripts.</summary>
    public string SessionId { get; private set; }

    /// <summary>The active telemetry log file, or null when telemetry is disabled.</summary>
    public string? LogFilePath { get; }

    /// <summary>
    /// The merged (user + project) list of hooks loaded from settings at construction time,
    /// with scope and enabled annotations applied. Empty when no hooks are configured.
    /// Exposed so the serve layer can enumerate hooks without direct access to the settings file.
    /// </summary>
    public IReadOnlyList<UserHook> ConfiguredHooks => this.configuredHooks;

    private volatile SessionOptions options;

    /// <summary>
    /// Current options (mutable: provider/model/mode/goal can change between runs).
    /// Backed by a volatile field so a mutation from another thread — e.g. serve's
    /// <c>session/setGoal</c> handler running while a prompt is in flight — is published
    /// safely. <see cref="RunAsync(IReadOnlyList{ContentBlock}, IAgentSink, CancellationToken)"/>
    /// snapshots this at entry, so a concurrent write never disturbs the running turn; it
    /// takes effect on the next run.
    /// </summary>
    public SessionOptions Options
    {
        get => this.options;
        set => this.options = value;
    }

    public TodoStore Todos => this.todos;

    public ScheduledTaskStore Schedules => this.schedules;

    /// <summary>The session's task manager (subagent and shell tasks).</summary>
    public TaskManager Tasks => this.tasks;

    /// <summary>
    /// The stable cooperative execution gate for this session's main agent. An outside actor
    /// (e.g. the TUI) can <see cref="AgentExecutionGate.RequestPause"/> and await
    /// <see cref="AgentExecutionGate.WaitUntilPaused"/> to bring a running turn to rest at an
    /// iteration boundary, then release the lease to resume. Owned for the session's lifetime and
    /// passed to every turn's loop via the spec; inert until a pause is actually requested, so
    /// serve/headless behavior is unchanged.
    /// </summary>
    public AgentExecutionGate ExecutionGate { get; } = new();

    public IReadOnlyList<ChatMessage> History => this.history;

    /// <summary>Accumulated token usage across all RunAsync calls in this session.</summary>
    public TokenUsage SessionUsage => this.sessionUsage;

    /// <summary>
    /// An immutable, UI-facing snapshot of the session's runtime state: id, accumulated usage, the
    /// last observed goal outcome, and copied todo / scheduled-task / background-task / LSP-server
    /// lists. Carries no mutable engine instances, so the TUI can diff and render it safely.
    /// </summary>
    public SessionRuntimeSnapshot GetRuntimeSnapshot()
    {
        return new SessionRuntimeSnapshot(
            this.SessionId,
            this.sessionUsage,
            this.lastGoalStatus,
            [.. this.todos.Items],
            [.. this.schedules.Items],
            MapTaskSnapshots(this.tasks.List()),
            this.lspManager?.GetSnapshot() ?? [],
            // A fresh, copied projection of live schedule execution states; no mutable runtime leaks.
            this.scheduleRuntime?.GetSnapshot() ?? []);
    }

    private static IReadOnlyList<BackgroundTaskSnapshot> MapTaskSnapshots(IReadOnlyList<TaskSnapshot> tasks)
    {
        var result = new BackgroundTaskSnapshot[tasks.Count];
        for (var i = 0; i < tasks.Count; i++)
        {
            result[i] = new BackgroundTaskSnapshot(tasks[i].Id, MapStatus(tasks[i].Status));
        }

        return result;
    }

    internal static BackgroundTaskStatus MapStatus(TaskRunStatus status) => status switch
    {
        TaskRunStatus.Running => BackgroundTaskStatus.Running,
        TaskRunStatus.Completed => BackgroundTaskStatus.Completed,
        TaskRunStatus.Failed => BackgroundTaskStatus.Failed,
        TaskRunStatus.Stopped => BackgroundTaskStatus.Stopped,
        _ => BackgroundTaskStatus.Running,
    };

    /// <summary>Clear the conversation.</summary>
    public void Reset() => this.history.Clear();

    /// <summary>
    /// Sets the reason reported in the <c>SessionEnd</c> hook payload. Call this before
    /// dispose when the exit is not a normal <c>/exit</c> — e.g. <c>"interrupt"</c> for
    /// keyboard interrupt, <c>"error"</c> for an unrecoverable error, or <c>"shutdown"</c>
    /// for a process-exit signal. The default is <c>"exit"</c>.
    /// </summary>
    public void SetSessionEndReason(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        this.sessionHooks.EndReason = reason;
    }

    /// <summary>
    /// Fires the <c>SessionEnd</c> hook immediately (bounded by the 2 s deadline) without
    /// waiting for the full <see cref="DisposeAsync"/>. Safe to call before or instead of
    /// <see cref="Dispose"/>/<see cref="DisposeAsync"/>: the idempotency guard in
    /// <see cref="FireSessionEndOnceAsync"/> ensures the hook fires at most once regardless
    /// of the call order. Intended for the process-exit path where the runtime may be shut
    /// down before the main-thread <c>using</c> block unwinds.
    /// </summary>
    public Task TriggerSessionEndAsync() => this.FireSessionEndOnceAsync();

    /// <summary>
    /// Replace the conversation with a persisted transcript and adopt its id, so subsequent
    /// transcript saves target the same file. Used to resume a session in a fresh process.
    /// </summary>
    public void Resume(string sessionId, IReadOnlyList<ChatMessage> messages) =>
        this.Resume(sessionId, messages, SessionMetadata.Empty);

    /// <summary>
    /// Replace the conversation with a persisted transcript and its metadata, preserving an explicit
    /// startup system-prompt override over the stored one.
    /// </summary>
    public void Resume(string sessionId, IReadOnlyList<ChatMessage> messages, SessionMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(metadata);

        this.sessionHooks.MarkResumed(this.SessionId);
        this.SessionId = sessionId;
        this.sessionHooks.SessionId = sessionId;
        this.history.Clear();
        this.history.AddRange(messages);
        this.Options = this.Options with
        {
            SystemPromptOverride = this.startupSystemPromptOverride ?? metadata.SystemPromptOverride,
        };
    }

    /// <summary>
    /// Adopt an existing session id so subsequent transcript/audit saves target its files, WITHOUT
    /// replacing history. Used by the TUI, whose history list is shared by reference (so
    /// <see cref="Resume"/>, which swaps history, is not appropriate there).
    /// </summary>
    /// <remarks>
    /// The <see cref="Tasks"/> manager keeps the session id it was constructed with, so already-open
    /// task logs are never moved to a new directory (which would be unsafe against live writers).
    /// Adoption happens at session bootstrap before any task is registered, so this grouping choice
    /// is not observable to running tasks. Task 6 revisits log grouping when the manager owns the
    /// runtime snapshot.
    /// </remarks>
    public void AdoptSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        this.SessionId = sessionId;
    }

    /// <summary>
    /// Run one user turn: stream the assistant reply (with tool use), keep the
    /// conversation, and return a structured result. On failure the turn is rolled
    /// back so history never corrupts.
    /// </summary>
    public Task<RunResult> RunAsync(string prompt, IAgentSink? sink = null, CancellationToken cancellationToken = default)
    {
        return this.RunAsync([new TextBlock(prompt)], sink, cancellationToken);
    }

    /// <summary>
    /// Posts a steering comment to the running (or next) turn. The comment is injected as a synthetic
    /// user message before the loop's next model call, so the orchestrator can redirect a turn already
    /// in flight. Safe to call concurrently with a running turn; no-op semantics if nothing is running
    /// (the comment is delivered to the next turn).
    /// </summary>
    public string? Steer(string comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);
        return this.steeringInbox.Enqueue(comment)?.Id;
    }

    /// <summary>
    /// Discards any steering comments not consumed by the just-finished turn, so a steer that raced
    /// with turn end cannot leak into the next, unrelated turn. Called at the turn boundary by the host.
    /// </summary>
    public void ClearSteering() => this.steeringInbox.Clear();

    /// <summary>Atomically removes and returns all still-pending steering messages in FIFO order.</summary>
    public IReadOnlyList<SteeringEntry> RecallSteering() => this.steeringInbox.RecallAll();

    /// <summary>
    /// Run one user turn using a pre-built list of content blocks (e.g. images + text).
    /// The blocks become the content of the user message added to history.
    /// On failure the turn is rolled back so history never corrupts.
    /// </summary>
    public async Task<RunResult> RunAsync(IReadOnlyList<ContentBlock> userContent, IAgentSink? sink = null, CancellationToken cancellationToken = default)
    {
        // A previous natural turn seals its inbox. Reopen before publishing the next loop spec,
        // without dropping a steer that raced a serve host's transition into this turn.
        this.steeringInbox.OpenForTurn();

        // Upgrade the session runner with prompt/agent handlers before SessionStart fires.
        // Best-effort: if the client cannot be created the runner stays as-is (prompt hooks will
        // log "handler not configured" and fail-open, which is acceptable).
        if (this.sessionHookRunner is not null && !this.sessionRunnerHandlersUpgraded && this.userHookRunnerOverride is null)
        {
            try
            {
                var earlyOpts = this.ResolveEffectiveOptions();
                var earlyClient = this.llmClientFactory.Create(
                    earlyOpts.ProviderId, this.credentials, this.fingerprint, this.http,
                    this.loggerFactory, earlyOpts.LlmHttpTimeoutOverride, this.StreamProgressSink);
                if (earlyClient is not null)
                {
                    var earlySettings = Coda.Agent.Settings.SettingsLoader.Load(earlyOpts.WorkingDirectory);
                    this.RebuildSessionRunnerWithHandlers(earlyClient, earlySettings, earlyOpts);
                    this.sessionRunnerHandlersUpgraded = true;
                }
            }
            catch
            {
                // Best-effort: proceed with the partially-wired runner.
            }
        }

        // Idempotent SessionStart hook: fires once regardless of whether InitializeAsync was called
        // explicitly (composition sites) or skipped (headless callers). Does NOT start the schedule
        // runtime or LSP — those require an explicit InitializeAsync call.
        await this.ApplySessionStartHookAsync(cancellationToken).ConfigureAwait(false);

        // Inject additionalContext from SessionStart exactly once, before the first user turn.
        if (this.sessionHooks.TakeAdditionalContextOnce() is { } addCtx)
        {
            this.history.Add(new ChatMessage(ChatRole.User, [new TextBlock(addCtx)]));
        }

        // Run the initial user message from SessionStart before the real user's turn.
        // The pending message is cleared atomically to guard against re-entry.
        var initialMsg = this.sessionHooks.TakeInitialUserMessage();
        if (initialMsg is not null)
        {
            try
            {
                await this.RunAsync([new TextBlock(initialMsg)], sink, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A failing initial-message turn is non-fatal; proceed with the real user's turn.
            }
        }

        var rootToolActivity = ToolActivityContext.CreateRoot();
        var recording = new RecordingSink(sink);
        ToolActivitySummary? completedToolActivity = null;
        var toolActivityFinalized = false;

        ToolActivitySummary? CompleteToolActivity(bool interrupted)
        {
            if (toolActivityFinalized)
            {
                return completedToolActivity;
            }

            toolActivityFinalized = true;
            completedToolActivity = recording.CompleteActivity(interrupted);
            return completedToolActivity;
        }

        SessionOptions options;
        AgentLoopSpec loopSpec;
        IAgentLoop loop;
        ILlmClient client;
        try
        {
            options = this.ResolveEffectiveOptions();
            var resolvedClient = this.llmClientFactory.Create(options.ProviderId, this.credentials, this.fingerprint, this.http, this.loggerFactory, options.LlmHttpTimeoutOverride, this.StreamProgressSink);
            if (resolvedClient is null)
            {
                return new RunResult(false, string.Empty, [], null, $"No chat client for provider '{options.ProviderId}'.")
                {
                    RootTurnId = rootToolActivity.RootTurnId,
                    ToolActivity = CompleteToolActivity(interrupted: false),
                };
            }

            client = resolvedClient;
            // Load allow/deny rules, user hooks, and goal/LSP settings once for the turn, then delegate
            // the per-turn assembly (agent options, permission stack, goal supervisor, tools, subagent
            // host, and the loop spec) to the pipeline builder.
            var settings = SettingsLoader.Load(options.WorkingDirectory);
            loopSpec = this.turnPipelineBuilder.BuildSpec(options, client, settings) with
            {
                Steering = this.steeringInbox,
                // Record on the go: the loop persists the transcript after every turn/tool cycle, so a
                // session killed mid-run still leaves a record (not just the once-at-the-end save below).
                PersistTurnAsync = this.PersistTranscriptAsync,
                // The stable per-session cooperative gate: lets an outside actor pause the loop at an
                // iteration boundary. Inert unless a pause is requested, so serve/headless are unchanged.
                Gate = this.ExecutionGate,
                ToolActivity = rootToolActivity,
            };
            // Wire per-turn cache-hygiene and 1h-TTL state into the loop options.
            loopSpec = loopSpec with
            {
                Options = loopSpec.Options with
                {
                    PreviousSystemPrompt = this.lastResolvedSystemPrompt,
                    UseOnehourTtl = settings.CacheUse1hTtl,
                },
            };
            // lastResolvedSystemPrompt is updated AFTER turnShape is finalized (inside the execution
            // gate block, after session-level appends are merged) so it captures base + append.
            // Test seam: override the settings-derived hook runner when injected. Null in production.
            if (this.userHookRunnerOverride is not null)
            {
                loopSpec = loopSpec with { UserHooks = this.userHookRunnerOverride };
            }
            loop = this.agentLoopFactory.Create(loopSpec);
        }
        catch
        {
            CompleteToolActivity(interrupted: true);
            throw;
        }

        // Snapshot BEFORE any agentic work. Reassigned after compaction so a turn failure rolls back
        // only the turn's own user message, never a successful compaction. If compaction itself
        // faults, history is left untouched (it mutates only after its model call returns), so this
        // pre-compaction count still makes rollback a safe no-op.
        var snapshot = this.history.Count;

        try
        {
            // One execution scope spans ALL agentic work in the turn: pre-turn auto-compaction (a
            // forked model call) AND the agent loop. IsExecuting stays true for the whole span, so a
            // pause requested during compaction is not reported reached until a safe boundary or the
            // turn ends. The scope closes BEFORE persistence (non-agentic) and on success, error, OR
            // cancel — if the turn ends before offering a boundary the gate still reports "reached".
            using (this.ExecutionGate.BeginExecution())
            {
                // Set the compaction hook runner from the turn's loopSpec (which already applies
                // the test-seam override). CompactHistoryAsync reads this field so both the pre-turn
                // path (here) and the in-loop delegate (TurnPipelineBuilder) share the same runner.
                this.compactionHooks = loopSpec.UserHooks;

                if (options.AutoCompactTokenThreshold > 0
                    && this.history.Count > 0
                    && TokenEstimator.Estimate(this.history) > options.AutoCompactTokenThreshold)
                {
                    var didCompact = await this.CompactHistoryAsync(client, options.Model, "auto", recording, cancellationToken).ConfigureAwait(false);

                    // After pre-turn compaction, re-inject any skill bodies that were previously
                    // loaded so the model does not silently lose its skills after compaction.
                    // PostCompact additionalContext was already injected inside CompactHistoryAsync.
                    // Order: PostCompact context first (inside CompactHistoryAsync), then skill
                    // re-attach here — skill bodies are closest to the model's next turn.
                    if (didCompact)
                    {
                        var reattachContent = options.SkillReattachContentProvider?.Invoke(options.AutoCompactTokenThreshold);
                        if (!string.IsNullOrEmpty(reattachContent))
                        {
                            // Skip if adding reattach would bring history back up to the threshold,
                            // which would trigger compaction again on the next iteration.
                            var postCompactTokens = TokenEstimator.Estimate(this.history);
                            var reattachTokenEstimate = reattachContent.Length / 4;
                            var wouldExceedThreshold = postCompactTokens + reattachTokenEstimate >= options.AutoCompactTokenThreshold;

                            // Skip if reattach is already the trailing message (exactly-once guard).
                            var alreadyLastMessage = this.history.Count > 0
                                && this.history[^1].Role == ChatRole.User
                                && this.history[^1].Content is [TextBlock tbLast]
                                && tbLast.Text == reattachContent;

                            if (!wouldExceedThreshold && !alreadyLastMessage)
                            {
                                this.history.Add(new ChatMessage(ChatRole.User, [new TextBlock(reattachContent)]));
                            }
                        }
                    }
                }

                snapshot = this.history.Count;

                // USER PROMPT SUBMIT HOOK GATE: fires before the message is appended to history
                // (§10 Phase 1 of the agent-hooks proposal). A fail-closed hook that blocks must
                // prevent both the append and the loop, returning a clean non-exceptional RunResult.
                TurnShape? turnShape = null;
                var effectiveContent = userContent;

                if (loopSpec.UserHooks is { } submitHooks && submitHooks.HasUserPromptSubmit)
                {
                    var originalPrompt = ExtractPromptText(userContent);
                    var submitResult = await submitHooks.RunUserPromptSubmitAsync(
                        originalPrompt,
                        ExtractAttachmentKinds(userContent),
                        this.history.Count,
                        options.Model,
                        PermissionModeToString(options.PermissionMode),
                        cancellationToken).ConfigureAwait(false);

                    if (submitResult.Block)
                    {
                        return new RunResult(false, string.Empty, [], null, submitResult.Reason ?? "blocked by hook")
                        {
                            RootTurnId = rootToolActivity.RootTurnId,
                            ToolActivity = CompleteToolActivity(interrupted: false),
                        };
                    }

                    if (submitResult.ModifiedPrompt is not null)
                    {
                        recording.OnPromptRewritten(
                            submitResult.ModifiedByHookCommand ?? string.Empty,
                            originalPrompt,
                            submitResult.ModifiedPrompt);
                        effectiveContent = ReplacePromptText(userContent, submitResult.ModifiedPrompt);
                    }

                    turnShape = submitResult.Shape;

                    this.history.Add(new ChatMessage(ChatRole.User, effectiveContent));

                    // additionalContext: a separate synthetic user message appended after the
                    // main user message (not merged into it — mirrors the LSP diagnostics seam).
                    if (submitResult.AdditionalContext is not null)
                    {
                        this.history.Add(new ChatMessage(ChatRole.User, [new TextBlock(submitResult.AdditionalContext)]));
                    }
                }
                else
                {
                    this.history.Add(new ChatMessage(ChatRole.User, userContent));
                }

                // Merge session-level appendSystemPrompt from SessionStart into the turn shape.
                // Session append (the "base") comes first; per-turn append follows.
                turnShape = this.sessionHooks.ComposeAppendSystemPrompt(turnShape);

                // M3: store the fully-resolved system prompt (base + any appends) so the next
                // turn receives it as PreviousSystemPrompt and can compare like-for-like.
                this.lastResolvedSystemPrompt = ResolveSystemPrompt(loopSpec.Options.SystemPrompt, turnShape);

                await loop.RunAsync(this.history, recording, cancellationToken, turnShape).ConfigureAwait(false);
            }

            await this.PersistTranscriptAsync(cancellationToken).ConfigureAwait(false);
            await this.PersistAuditTurnAsync(options, recording, loopSpec.Options.SystemPrompt, loopSpec.Tools.Definitions, cancellationToken).ConfigureAwait(false);
            this.sessionUsage = this.sessionUsage.Add(recording.Usage);
            this.sessionHooks.RecordTurn();

            // Zero-counters warning: if both cache counters have been zero for several turns in a
            // row the prefix is likely below the per-model minimum and cache is silently inactive.
            // Emit once per session so the user has a chance to notice without repeated noise.
            // Skipped for the Copilot provider: it does not report cache counters, so HasCacheActivity
            // is permanently false and would fire a spurious warning on every Copilot session.
            if (!this.cacheZeroActivityWarned
                && !string.Equals(options.ProviderId, GitHubCopilotProvider.Id, StringComparison.Ordinal))
            {
                if (recording.Usage.HasCacheActivity)
                {
                    this.turnsWithZeroCacheActivity = 0;
                }
                else
                {
                    this.turnsWithZeroCacheActivity++;
                    if (this.turnsWithZeroCacheActivity >= ZeroActivityWarnAfterTurns)
                    {
                        this.cacheZeroActivityWarned = true;
                        sink?.OnWarning(
                            "Prompt cache appears inactive: both cache read and write counters " +
                            "have been zero for several consecutive turns. " +
                            "The prefix may be below the per-model minimum size.");
                    }
                }
            }

            this.FireIdleNotificationBackground();
            var toolActivity = CompleteToolActivity(interrupted: false);
            return new RunResult(true, recording.FinalText, recording.ToolCalls, recording.StopReason, null)
            {
                Usage = recording.Usage,
                Goal = loop.LastGoalStatus,
                RootTurnId = rootToolActivity.RootTurnId,
                ToolActivity = toolActivity,
            };
        }
        catch (OperationCanceledException)
        {
            var toolActivity = CompleteToolActivity(interrupted: true);
            this.Rollback(snapshot);
            this.steeringInbox.Clear();
            return new RunResult(false, recording.FinalText, recording.ToolCalls, null, "Canceled.")
            {
                Usage = recording.Usage,
                Goal = loop.LastGoalStatus,
                RootTurnId = rootToolActivity.RootTurnId,
                ToolActivity = toolActivity,
            };
        }
        catch (Exception ex)
        {
            var toolActivity = CompleteToolActivity(interrupted: true);
            this.Rollback(snapshot);
            this.steeringInbox.Clear();
            this.LogTurnFailed(options.ProviderId, options.Model, ex.GetType().Name, ex.Message);
            return new RunResult(false, recording.FinalText, recording.ToolCalls, null, ex.Message)
            {
                Usage = recording.Usage,
                Goal = loop.LastGoalStatus,
                RootTurnId = rootToolActivity.RootTurnId,
                ToolActivity = toolActivity,
            };
        }
        finally
        {
            CompleteToolActivity(interrupted: true);
            // Remember the most recent goal outcome so GetRuntimeSnapshot can surface it between
            // turns. Only overwrite on a non-null result so a subsequent goal-less turn does not
            // erase the last real goal status.
            if (loop.LastGoalStatus is not null)
            {
                this.lastGoalStatus = loop.LastGoalStatus;
            }
        }
    }

    /// <summary>
    /// The session options with the auto-compaction threshold resolved from the model's real
    /// context window (see <see cref="ModelLimits.ResolveAutoCompactThreshold"/>). 0 (the default)
    /// means "derive from the window"; an explicit positive value overrides.
    /// </summary>
    private SessionOptions ResolveEffectiveOptions()
    {
        var options = this.Options;
        return options with
        {
            AutoCompactTokenThreshold = ModelLimits.ResolveAutoCompactThreshold(
                ModelCatalog.Default, options.ProviderId, options.Model, options.AutoCompactTokenThreshold),
            // Inject the session-resolved tool filter so BuildParentTools picks it up without
            // needing a separate TurnPipelineBuilder parameter. Options-level filter wins if set
            // directly (test override); otherwise this seeds from the settings-derived value.
            AgentToolFilter = this.agentToolFilter,
        };
    }

    /// <summary>Summarize the conversation in place (used by auto-compaction and the /compact command).</summary>
    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        var options = this.ResolveEffectiveOptions();
        var client = this.llmClientFactory.Create(options.ProviderId, this.credentials, this.fingerprint, this.http, this.loggerFactory, options.LlmHttpTimeoutOverride, this.StreamProgressSink);
        if (client is null)
        {
            return;
        }

        // Upgrade session runner with prompt/agent handlers now that we have a client.
        // This is the manual /compact path; RunAsync may not have been called yet.
        if (this.sessionHookRunner is not null && !this.sessionRunnerHandlersUpgraded && this.userHookRunnerOverride is null)
        {
            var compactSettings = Coda.Agent.Settings.SettingsLoader.Load(options.WorkingDirectory);
            this.RebuildSessionRunnerWithHandlers(client, compactSettings, options);
            this.sessionRunnerHandlersUpgraded = true;
        }

        // For the manual /compact path, use the session hook runner (same as the per-turn runner
        // for hooks that apply to the full session). The compactionHooks field may not be set yet
        // if /compact is called before the first RunAsync turn.
        this.compactionHooks = this.userHookRunnerOverride ?? this.sessionHookRunner;
        await this.CompactHistoryAsync(client, options.Model, "manual", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The nominal context window used for the /context breakdown and percentage.</summary>
    public const int ContextWindowTokens = ContextWindow.DefaultTokens;

    /// <summary>
    /// The active tool-search coordinator, or <see langword="null"/> when tool search is not active
    /// (standard mode or before the first turn). Exposed so <c>/context</c> can measure what the
    /// live turn actually transmits rather than re-analyzing with an empty discovered set.
    /// </summary>
    public ToolSearchCoordinator? ToolSearchCoordinator => this.toolSearchCoordinator;

    /// <summary>
    /// Analyze how the context window is currently used, broken down by category
    /// (system prompt, tools, messages, reserved buffer, free space). Mirrors the
    /// reference client's <c>/context</c>. Uses the provider's count-tokens endpoint
    /// when available; otherwise falls back to a local character-based estimate
    /// (<see cref="ContextReport.IsExact"/> reports which).
    /// </summary>
    public async Task<ContextReport> AnalyzeContextAsync(CancellationToken cancellationToken = default)
    {
        var options = this.ResolveEffectiveOptions();
        var client = this.llmClientFactory.Create(options.ProviderId, this.credentials, this.fingerprint, this.http, this.loggerFactory, options.LlmHttpTimeoutOverride, this.StreamProgressSink);

        var systemPrompt = EffectiveSystemPrompt.Resolve(options);

        var registry = new ToolRegistry([.. BuiltInTools.All(), .. options.ExtraTools]);
        var allDefs = this.toolSearchCoordinator?.BuildWireDefinitions(registry) ?? registry.Definitions;
        // MCP tools are namespaced "mcp__<server>__<tool>"; everything else is built-in.
        var mcpDefs = allDefs.Where(d => d.Name.StartsWith("mcp__", StringComparison.Ordinal)).ToList();
        var builtinDefs = allDefs.Where(d => !d.Name.StartsWith("mcp__", StringComparison.Ordinal)).ToList();

        // Total MCP tools in the registry (wire + deferred), used only to compute deferredCount.
        var mcpAllCount = registry.Definitions.Count(d => d.Name.StartsWith("mcp__", StringComparison.Ordinal));
        // Number of MCP tools withheld from the wire (for the informational deferred category).
        var deferredCount = mcpAllCount - mcpDefs.Count;

        int systemTokens;
        int toolTokens;
        int mcpToolTokens;
        int messageTokens;
        var isExact = false;

        // Prefer the provider's count-tokens API. Counts are isolated by subtracting
        // a baseline (the synthetic dummy message count_tokens requires).
        var counted = false;
        if (client is not null)
        {
            var baseline = await client.CountTokensAsync(
                new ChatRequest { Model = options.Model, Messages = [] }, cancellationToken).ConfigureAwait(false);
            var systemCount = await client.CountTokensAsync(
                new ChatRequest { Model = options.Model, System = systemPrompt, Messages = [] }, cancellationToken).ConfigureAwait(false);
            var builtinCount = builtinDefs.Count > 0
                ? await client.CountTokensAsync(
                    new ChatRequest { Model = options.Model, Messages = [], Tools = builtinDefs }, cancellationToken).ConfigureAwait(false)
                : 0;
            var mcpCount = mcpDefs.Count > 0
                ? await client.CountTokensAsync(
                    new ChatRequest { Model = options.Model, Messages = [], Tools = mcpDefs }, cancellationToken).ConfigureAwait(false)
                : (int?)0;
            var messageCount = this.history.Count > 0
                ? await client.CountTokensAsync(
                    new ChatRequest { Model = options.Model, Messages = this.history }, cancellationToken).ConfigureAwait(false)
                : 0;

            if (baseline is not null && systemCount is not null && builtinCount is not null && mcpCount is not null && messageCount is not null)
            {
                systemTokens = Math.Max(0, systemCount.Value - baseline.Value);
                toolTokens = builtinDefs.Count > 0 ? Math.Max(0, builtinCount.Value - baseline.Value) : 0;
                mcpToolTokens = mcpDefs.Count > 0 ? Math.Max(0, mcpCount.Value - baseline.Value) : 0;
                messageTokens = Math.Max(0, messageCount.Value);
                isExact = true;
                counted = true;
            }
            else
            {
                (systemTokens, toolTokens, mcpToolTokens, messageTokens) = (0, 0, 0, 0);
            }
        }
        else
        {
            (systemTokens, toolTokens, mcpToolTokens, messageTokens) = (0, 0, 0, 0);
        }

        if (!counted)
        {
            systemTokens = systemPrompt.Length / 4;
            toolTokens = EstimateToolTokens(builtinDefs);
            mcpToolTokens = EstimateToolTokens(mcpDefs);
            messageTokens = TokenEstimator.Estimate(this.history);
        }

        var used = systemTokens + toolTokens + mcpToolTokens + messageTokens;

        // Resolve the model's real context window: prefer what the provider reports
        // live (authoritative — knows internal/special models the catalog doesn't),
        // then the catalog, then the nominal default. The live fetch is best-effort.
        IReadOnlyList<ModelInfo> liveModels = [];
        if (client is not null)
        {
            liveModels = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }

        var window = ResolveContextWindow(liveModels, options.ProviderId, options.Model, ModelCatalog.Default);

        // Reserved headroom shown for auto-compaction. Capped at the threshold itself
        // so it represents the compaction reserve rather than swallowing the whole
        // visualization for large-context models (e.g. a 1M window with a 100k
        // threshold would otherwise show a ~900k "buffer").
        var reserved = options.AutoCompactTokenThreshold > 0
            ? Math.Min(Math.Max(0, window - options.AutoCompactTokenThreshold), options.AutoCompactTokenThreshold)
            : 0;
        var free = Math.Max(0, window - used - reserved);

        var categories = new List<ContextCategory>();
        if (systemTokens > 0)
        {
            categories.Add(new ContextCategory("System prompt", systemTokens));
        }

        if (toolTokens > 0)
        {
            categories.Add(new ContextCategory("System tools", toolTokens));
        }

        if (mcpToolTokens > 0)
        {
            categories.Add(new ContextCategory("MCP tools", mcpToolTokens));
        }

        // Informational zero-token entry when tools are loaded but withheld from the wire.
        // Listed so users can see the servers are active without counting against UsedTokens.
        if (deferredCount > 0)
        {
            categories.Add(new ContextCategory($"MCP tools (deferred, {deferredCount} tools)", 0));
        }

        if (messageTokens > 0)
        {
            categories.Add(new ContextCategory("Messages", messageTokens));
        }

        if (reserved > 0)
        {
            categories.Add(new ContextCategory("Autocompact buffer", reserved));
        }

        categories.Add(new ContextCategory("Free space", free));

        return new ContextReport
        {
            Model = options.Model,
            MaxTokens = window,
            Categories = categories,
            UsedTokens = used,
            IsExact = isExact,
            MessageCount = this.history.Count,
        };
    }

    /// <summary>
    /// Resolve the model list for this session's provider: the provider's live list
    /// when available, otherwise the models.dev catalog, otherwise a built-in list.
    /// When <paramref name="refresh"/> is true, the catalog is refreshed from
    /// models.dev first. Best-effort and offline-safe. Shared by the TUI, headless,
    /// and serve front-ends.
    /// </summary>
    public async Task<ModelListResult> ListModelsAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        var options = this.Options;
        if (refresh)
        {
            await ModelCatalog.RefreshAsync(this.http, cancellationToken).ConfigureAwait(false);
        }

        var client = this.llmClientFactory.Create(options.ProviderId, this.credentials, this.fingerprint, this.http, this.loggerFactory, options.LlmHttpTimeoutOverride, this.StreamProgressSink);
        IReadOnlyList<ModelInfo> live = [];
        if (client is not null)
        {
            try
            {
                live = refresh
                    ? await client.RefreshModelsAsync(cancellationToken).ConfigureAwait(false)
                    : await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // The factory passed our shared HttpClient, which the client does not
                // own, so disposing the client never disposes our HttpClient.
                (client as IDisposable)?.Dispose();
            }
        }

        return ModelListBuilder.Build(options.ProviderId, live, ModelCatalog.Default);
    }

    /// <summary>
    /// Resolve the context-window size for a model: the live list's reported limit
    /// (authoritative, incl. internal/special models), then the catalog, then the
    /// nominal <see cref="ContextWindowTokens"/> default.
    /// </summary>
    public static int ResolveContextWindow(
        IReadOnlyList<ModelInfo> liveModels,
        string providerId,
        string model,
        ModelCatalog catalog)
    {
        var live = liveModels
            .FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase))?.ContextLimit;
        return live ?? catalog.Get(providerId, model)?.ContextLimit ?? ContextWindowTokens;
    }

    /// <summary>Char-based (~4 chars/token) estimate of a set of tool definitions.</summary>
    private static int EstimateToolTokens(IReadOnlyList<ToolDefinition> toolDefs)
    {
        var toolChars = 0L;
        foreach (var def in toolDefs)
        {
            toolChars += def.Name.Length + def.Description.Length + def.InputSchemaJson.Length;
        }

        return (int)(toolChars / 4);
    }

    private async Task<bool> CompactHistoryAsync(
        ILlmClient client,
        string model,
        string trigger,
        IAgentSink? sink,
        CancellationToken cancellationToken)
    {
        if (this.history.Count == 0)
        {
            return false;
        }

        var hooks = this.compactionHooks;
        var tokensBefore = TokenEstimator.Estimate(this.history);
        var messageCount = this.history.Count;

        // PreCompact hook: fail-open, but a "block" decision cancels this compaction attempt.
        // The caller must not immediately retry — the next trigger (auto threshold or /compact)
        // offers a fresh chance.
        if (hooks is { HasPreCompact: true })
        {
            PreCompactResult preResult;
            try
            {
                preResult = await hooks.RunPreCompactAsync(
                    trigger,
                    tokensBefore,
                    messageCount,
                    instructions: null,
                    depth: 0,
                    taskId: null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fail-open: a broken hook lets compaction proceed.
                preResult = PreCompactResult.Allow;
            }

            if (preResult.Block)
            {
                sink?.OnCompactionCancelled(preResult.ByHookCommand ?? string.Empty, trigger);
                return false; // compaction cancelled by hook
            }

            // Run compaction with possible instructions override.
            var service = new CompactionService(new ForkedAgentRunner(client, model));
            var (compacted, summary) = await service.CompactAsync(
                this.history,
                preResult.Instructions,
                cancellationToken).ConfigureAwait(false);

            if (!ReferenceEquals(compacted, this.history))
            {
                this.history.Clear();
                this.history.AddRange(compacted);

                // PostCompact hook: injects additional context before skill re-attachment.
                // Order: PostCompact context first, then skill re-attach — so skill bodies
                // are closest to the model's next turn (deterministic, documented ordering).
                if (hooks is { HasPostCompact: true } && summary is not null)
                {
                    await this.ApplyPostCompactHookAsync(
                        tokensBefore,
                        messageCount,
                        summary,
                        hooks,
                        sink,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            return !ReferenceEquals(compacted, this.history) || compacted.Count < messageCount;
        }
        else
        {
            // No PreCompact hooks: run compaction directly.
            var service = new CompactionService(new ForkedAgentRunner(client, model));
            var (compacted, summary) = await service.CompactAsync(
                this.history,
                instructionsOverride: null,
                cancellationToken).ConfigureAwait(false);

            if (!ReferenceEquals(compacted, this.history))
            {
                this.history.Clear();
                this.history.AddRange(compacted);

                if (hooks is { HasPostCompact: true } && summary is not null)
                {
                    await this.ApplyPostCompactHookAsync(
                        tokensBefore,
                        messageCount,
                        summary,
                        hooks,
                        sink,
                        cancellationToken).ConfigureAwait(false);
                }

                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Fires the <c>PostCompact</c> hook and injects <c>additionalContext</c> into history when
    /// present. The injection is budget-guarded: if adding the context would bring the token count
    /// back up to or beyond the compaction threshold, the injection is skipped so compaction is not
    /// immediately undone. Exactly-once: the check is inside <see cref="CompactHistoryAsync"/>.
    /// </summary>
    private async Task ApplyPostCompactHookAsync(
        int tokensBefore,
        int messageCount,
        string summary,
        UserHookRunner hooks,
        IAgentSink? sink,
        CancellationToken cancellationToken)
    {
        var tokensAfter = TokenEstimator.Estimate(this.history);
        PostCompactResult postResult;
        try
        {
            postResult = await hooks.RunPostCompactAsync(
                tokensBefore,
                tokensAfter,
                messageCount,
                summary,
                depth: 0,
                taskId: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fail-open: a broken PostCompact hook leaves history unchanged.
            return;
        }

        if (string.IsNullOrEmpty(postResult.AdditionalContext))
        {
            return;
        }

        // Budget guard: re-injecting more than compaction freed defeats the compaction.
        var options = this.ResolveEffectiveOptions();
        var threshold = options.AutoCompactTokenThreshold;
        if (threshold > 0)
        {
            var contextTokens = postResult.AdditionalContext.Length / 4;
            if (tokensAfter + contextTokens >= threshold)
            {
                return;
            }
        }

        this.history.Add(new ChatMessage(ChatRole.User, [new TextBlock(postResult.AdditionalContext)]));
        sink?.OnPostCompactContextInjected(postResult.AdditionalContext);
    }

    private async Task PersistTranscriptAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = this.Options;
            this.transcriptStore ??= new SessionTranscriptStore(
                options.WorkingDirectory,
                this.loggerFactory.CreateLogger<SessionTranscriptStore>());
            await this.transcriptStore.SaveAsync(
                this.SessionId,
                this.history,
                new SessionMetadata { SystemPromptOverride = options.SystemPromptOverride },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Transcript persistence must never break a turn.
            this.LogTranscriptPersistFailed(this.SessionId, ex);
        }
    }

    private async Task PersistAuditTurnAsync(SessionOptions options, RecordingSink recording, string systemPrompt, IReadOnlyList<ToolDefinition> toolDefs, CancellationToken cancellationToken)
    {
        try
        {
            this.auditStore ??= new SessionAuditStore(options.WorkingDirectory);

            // Seed / re-seed the per-session turn counter from the sidecar so indices stay monotonic
            // across resume (a fresh process) and across an in-life id adoption (TUI /resume).
            if (this.auditCounterForId != this.SessionId)
            {
                this.auditTurnIndex = (await this.auditStore.LoadAsync(this.SessionId, cancellationToken).ConfigureAwait(false)).Count;
                this.auditCounterForId = this.SessionId;
            }

            var turn = new SessionAuditTurn
            {
                TurnIndex = this.auditTurnIndex++,
                TsUtc = DateTime.UtcNow,
                Provider = options.ProviderId,
                Model = options.Model,
                InputTokens = recording.Usage.TotalInputTokens,
                OutputTokens = recording.Usage.OutputTokens,
                StopReason = recording.StopReason,
                ToolCalls = recording.ToolCalls,
                SystemPrompt = systemPrompt,
                ToolDefs = toolDefs,
            };
            await this.auditStore.AppendTurnAsync(this.SessionId, turn, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Audit persistence is best-effort and must never break a turn (same policy as the transcript).
            this.LogAuditPersistFailed(this.SessionId, ex);
        }
    }

    private void Rollback(int snapshot)
    {
        if (this.history.Count > snapshot)
        {
            this.history.RemoveRange(snapshot, this.history.Count - snapshot);
        }
    }

    // -------------------------------------------------------------------------
    // UserPromptSubmit helpers
    // -------------------------------------------------------------------------

    private static string ExtractPromptText(IReadOnlyList<ContentBlock> content)
    {
        var parts = new List<string>();
        foreach (var block in content)
        {
            if (block is TextBlock tb)
            {
                parts.Add(tb.Text);
            }
        }

        return string.Concat(parts);
    }

    private static IReadOnlyList<string> ExtractAttachmentKinds(IReadOnlyList<ContentBlock> content)
    {
        var kinds = new List<string>();
        foreach (var block in content)
        {
            if (block is ImageBlock)
            {
                kinds.Add("image");
            }
            else if (block is not TextBlock)
            {
                // Forward-compatible: surface other non-text block types by lowercased type name
                // without the "Block" suffix (e.g. "thinkingBlock" → "thinking").
                var typeName = block.GetType().Name;
                var suffix = "Block";
                var kind = typeName.EndsWith(suffix, StringComparison.Ordinal)
                    ? typeName[..^suffix.Length].ToLowerInvariant()
                    : typeName.ToLowerInvariant();
                kinds.Add(kind);
            }
        }

        return kinds.AsReadOnly();
    }

    /// <summary>
    /// Replaces all <see cref="TextBlock"/> instances in <paramref name="content"/> with a
    /// single <see cref="TextBlock"/> containing <paramref name="modifiedPrompt"/>. Non-text
    /// blocks are preserved in their original positions. The replacement is inserted at the
    /// location of the first text block; if there are no text blocks, it is prepended.
    /// </summary>
    private static IReadOnlyList<ContentBlock> ReplacePromptText(
        IReadOnlyList<ContentBlock> content,
        string modifiedPrompt)
    {
        var result = new List<ContentBlock>(content.Count);
        var replaced = false;
        foreach (var block in content)
        {
            if (block is TextBlock)
            {
                if (!replaced)
                {
                    result.Add(new TextBlock(modifiedPrompt));
                    replaced = true;
                }
                // Skip subsequent TextBlocks — all are subsumed by the single modified prompt.
            }
            else
            {
                result.Add(block);
            }
        }

        if (!replaced)
        {
            result.Insert(0, new TextBlock(modifiedPrompt));
        }

        return result.AsReadOnly();
    }

    internal static string PermissionModeToString(PermissionMode mode) => mode switch
    {
        PermissionMode.Default           => "default",
        PermissionMode.AcceptEdits       => "acceptEdits",
        PermissionMode.Plan              => "plan",
        PermissionMode.BypassPermissions => "bypassPermissions",
        _                                => mode.ToString().ToLowerInvariant(),
    };

    // -------------------------------------------------------------------------
    // Session lifecycle helpers (Phase 2)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds <see cref="sessionHookRunner"/> adding a <c>promptHandler</c> now that an
    /// <see cref="ILlmClient"/> is available. The executor override and all other construction
    /// parameters are preserved from the original build so test seams stay effective.
    /// Called at most once per session lifetime (guarded by <see cref="sessionRunnerHandlersUpgraded"/>).
    /// Not called when <see cref="userHookRunnerOverride"/> is set (it already has whatever
    /// handlers the test/production caller wants).
    /// </summary>
    private void RebuildSessionRunnerWithHandlers(
        ILlmClient client,
        Coda.Agent.Settings.CodaSettings settings,
        SessionOptions options)
    {
        var httpHandler = new Coda.Agent.Hooks.HttpHookHandler(
            httpClient: null,
            settings.HttpHookAllowlist,
            logger: this.loggerFactory.CreateLogger("Coda.Hooks.Http"));
        var promptHandler = new Coda.Agent.Hooks.PromptHookHandler(
            new Coda.Agent.Watchers.ForkedAgentRunner(client, options.Model),
            logger: this.loggerFactory.CreateLogger("Coda.Hooks.Prompt"));
        this.sessionHookRunner = new UserHookRunner(
            this.configuredHooks,
            execOverride: this.sessionExecOverride,
            context: new HookContext(this.SessionId, options.WorkingDirectory),
            logger: this.loggerFactory.CreateLogger("Coda.Hooks.Session"),
            httpHandler: httpHandler,
            promptHandler: promptHandler,
            trustGuard: this.trustGuard,
            runLog: this.hookRunLog);
    }

    private static bool IsSessionLevelHookEvent(string eventName) =>
        string.Equals(eventName, "SessionStart", StringComparison.OrdinalIgnoreCase)
        || string.Equals(eventName, "SessionEnd", StringComparison.OrdinalIgnoreCase)
        || string.Equals(eventName, "Notification", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the fully-resolved system prompt: <paramref name="basePrompt"/> with any
    /// <see cref="TurnShape.AppendSystemPrompt"/> appended, matching the logic in
    /// <c>TurnShapeResolver</c>. Stored as <c>lastResolvedSystemPrompt</c> so the next turn
    /// receives a like-for-like <see cref="AgentOptions.PreviousSystemPrompt"/>.
    /// </summary>
    private static string ResolveSystemPrompt(string basePrompt, TurnShape? shape) =>
        shape?.AppendSystemPrompt is { } append ? $"{basePrompt}\n\n{append}" : basePrompt;

    /// <summary>
    /// Applies SessionStart hook outputs. Called once from <see cref="InitializeCoreAsync"/>.
    /// Fail-open: a broken or timed-out hook is logged and ignored.
    /// </summary>
    internal Task ApplySessionStartHookAsync(CancellationToken cancellationToken)
    {
        var options = this.Options;
        return this.sessionHooks.ApplySessionStartAsync(
            new SessionStartPayloadContext(
                options.Model,
                PermissionModeToString(options.PermissionMode),
                this.SessionTranscriptPath(options)),
            cancellationToken);
    }

    /// <summary>The path this session's transcript is written to.</summary>
    private string SessionTranscriptPath(SessionOptions options) =>
        Path.Combine(options.WorkingDirectory, ".coda", "sessions", $"{this.SessionId}.json");

    /// <summary>
    /// Fires SessionEnd hooks exactly once. Hard-coded 2 s deadline; never throws.
    /// </summary>
    private Task FireSessionEndOnceAsync() =>
        this.sessionHooks.FireSessionEndOnceAsync(
            this.sessionUsage,
            this.SessionTranscriptPath(this.Options));

    /// <summary>
    /// Fires a <c>Notification("idle")</c> hook in the background after a successful turn.
    /// Fire-and-forget so notification latency never blocks the caller.
    /// </summary>
    private void FireIdleNotificationBackground() =>
        this.sessionHooks.FireIdleNotificationBackground();

    /// <summary>
    /// Asynchronously tears the session down: shuts down LSP servers (bounded by
    /// <see cref="LspDisposeTimeout"/>) without any sync-over-async
    /// blocking, then releases the owned HTTP client and logger factory. This is the path
    /// <c>coda serve</c> uses — see <c>ServeHost</c>, which awaits it from its run loop so a
    /// not-fully-disposed session never leaks across turns.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Stop and drain in-flight background Notification hooks BEFORE SessionEnd fires: an idle
        // notification subprocess that outlived SessionEnd would invert the documented ordering.
        await this.sessionHooks.DrainBackgroundNotificationsAsync().ConfigureAwait(false);

        // Fire SessionEnd exactly once before teardown begins. The 2 s hard ceiling is
        // enforced inside FireSessionEndOnceAsync; it never throws.
        await this.FireSessionEndOnceAsync().ConfigureAwait(false);

        // Prevent/await the initialization race and dispose the schedule runtime FIRST: a due firing
        // can then never register work after the task-manager shutdown below has begun. The runtime's
        // own disposal cancels its loop and returns promptly, so this stays bounded.
        await this.ShutdownScheduleRuntimeAsync().ConfigureAwait(false);

        // Graceful, bounded shutdown of all subagent/shell tasks: cancels running work, kills shell
        // process trees, waits the dispose budget, then force-stops stragglers. Idempotent.
        await this.tasks.DisposeAsync().ConfigureAwait(false);

        // Shut down LSP servers before releasing the HTTP client.
        if (this.lspManager is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(LspDisposeTimeout);
                await this.lspManager.ShutdownAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Best-effort — swallow on dispose.
                this.LogLspShutdownFailed(this.SessionId, ex);
            }
        }

        this.ownedHttpClient?.Dispose();
        this.loggerFactory.Dispose();
        this.sessionHooks.Dispose();
    }

    /// <summary>
    /// Synchronous dispose for non-async callers (the TUI / headless commands). Delegates to
    /// <see cref="DisposeAsync"/> on a worker thread, bounded by <see cref="SyncDisposeBudget"/>
    /// (the TaskManager shutdown budget plus <see cref="LspDisposeTimeout"/>), so it never blocks
    /// the caller indefinitely yet still lets HTTP/logger/LSP disposal finish before returning.
    /// Async callers (serve) should prefer <see cref="DisposeAsync"/>.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Task.Run(() => this.DisposeAsync().AsTask()).Wait(SyncDisposeBudget);
        }
        catch (Exception ex)
        {
            // Best-effort — swallow on dispose.
            this.LogSyncDisposeFailed(this.SessionId, ex);
        }
    }
}
