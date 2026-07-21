using Coda.Agent;
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Tasks;
using Coda.Agent.Compaction;
using Coda.Agent.Goals;
using Coda.Agent.Lsp;
using Coda.Agent.OutputStyles;
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
    private readonly LspServerManager? lspManager;
    private readonly LspDiagnosticRegistry? lspDiagnostics;
    private readonly ToolSearchCoordinator? toolSearchCoordinator;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly TurnPipelineBuilder turnPipelineBuilder;
    private readonly SteeringInbox steeringInbox = new();
    private TokenUsage sessionUsage = TokenUsage.Zero;
    private GoalStatus? lastGoalStatus;

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
        TimeProvider? timeProvider = null)
    {
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.fingerprint = fingerprint ?? new ClientFingerprint();
        this.llmClientFactory = llmClientFactory ?? new DefaultLlmClientFactory();
        this.agentLoopFactory = agentLoopFactory ?? new DefaultAgentLoopFactory();
        // Live options accessor for scheduled firings. Defaults to the current volatile Options (not a
        // construction snapshot), so a mid-session model/effort/tool/permission change is picked up.
        this.currentOptionsProvider = currentOptionsProvider ?? (() => this.Options);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.history = history ?? [];
        this.SessionId = sessionId ?? SessionIds.NewId();
        // The manager groups persistent task logs under the session id captured HERE. If the id
        // is later adopted (AdoptSessionId/Resume), the manager keeps this original grouping so
        // active task logs are never moved out from under open writers — see AdoptSessionId.
        this.tasks = new TaskManager(this.SessionId);
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

        // Load LSP servers from settings and merge with any plugin-contributed servers.
        // Plugin keys are namespaced (plugin:<name>:<server>) so clashes with settings keys are rare;
        // settings always win on exact-key clashes.
        var settings = SettingsLoader.Load(options.WorkingDirectory);

        var userCodaPluginsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".coda",
            "plugins");
        var projectCodaPluginsDir = Path.Combine(options.WorkingDirectory, ".coda", "plugins");
        var lspServers = LspServerMapBuilder.Build(
            settings.LspServers,
            [userCodaPluginsDir, projectCodaPluginsDir]);

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

        // Built last so that if any wiring above throws, no telemetry file handle is
        // opened and then leaked (the session is never returned, so Dispose never runs).
        // A per-session TelemetryOverride (e.g. `coda serve --telemetry`) wins over the
        // settings-file block; environment overrides still layer on top via Resolve.
        var loggerSetup = CodaLoggerFactory.Create(
            TelemetryResolver.Resolve(this.options.TelemetryOverride ?? settings.Telemetry));
        this.loggerFactory = loggerSetup.Factory;
        this.LogFilePath = loggerSetup.LogFilePath;
        this.logger = this.loggerFactory.CreateLogger("Coda.Session");

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
            this.CompactHistoryAsync,
            // Evaluated per turn, so once InitializeAsync starts the runtime the main schedule_list
            // sees the live view; it returns null before initialization and when scheduling is off.
            () => this.scheduleRuntime);

        this.logger.LogInformation(
            "Session {sessionId} started: provider {provider}, model {model}",
            this.SessionId, options.ProviderId, options.Model);
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
    /// Replace the conversation with a persisted transcript and adopt its id, so subsequent
    /// transcript saves target the same file. Used to resume a session in a fresh process.
    /// </summary>
    public void Resume(string sessionId, IReadOnlyList<ChatMessage> messages)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(messages);

        this.SessionId = sessionId;
        this.history.Clear();
        this.history.AddRange(messages);
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
    public void Steer(string comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);
        this.steeringInbox.Enqueue(comment);
    }

    /// <summary>
    /// Discards any steering comments not consumed by the just-finished turn, so a steer that raced
    /// with turn end cannot leak into the next, unrelated turn. Called at the turn boundary by the host.
    /// </summary>
    public void ClearSteering() => this.steeringInbox.Clear();

    /// <summary>
    /// Run one user turn using a pre-built list of content blocks (e.g. images + text).
    /// The blocks become the content of the user message added to history.
    /// On failure the turn is rolled back so history never corrupts.
    /// </summary>
    public async Task<RunResult> RunAsync(IReadOnlyList<ContentBlock> userContent, IAgentSink? sink = null, CancellationToken cancellationToken = default)
    {
        var options = this.ResolveEffectiveOptions();
        var client = this.llmClientFactory.Create(options.ProviderId, this.credentials, this.fingerprint, this.http, this.loggerFactory, options.LlmHttpTimeoutOverride, this.StreamProgressSink);
        if (client is null)
        {
            return new RunResult(false, string.Empty, [], null, $"No chat client for provider '{options.ProviderId}'.");
        }

        // Load allow/deny rules, user hooks, and goal/LSP settings once for the turn, then delegate
        // the per-turn assembly (agent options, permission stack, goal supervisor, tools, subagent
        // host, and the loop spec) to the pipeline builder.
        var settings = SettingsLoader.Load(options.WorkingDirectory);
        var loopSpec = this.turnPipelineBuilder.BuildSpec(options, client, settings) with
        {
            Steering = this.steeringInbox,
            // Record on the go: the loop persists the transcript after every turn/tool cycle, so a
            // session killed mid-run still leaves a record (not just the once-at-the-end save below).
            PersistTurnAsync = this.PersistTranscriptAsync,
            // The stable per-session cooperative gate: lets an outside actor pause the loop at an
            // iteration boundary. Inert unless a pause is requested, so serve/headless are unchanged.
            Gate = this.ExecutionGate,
        };
        var loop = this.agentLoopFactory.Create(loopSpec);

        var recording = new RecordingSink(sink);

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
                if (options.AutoCompactTokenThreshold > 0
                    && this.history.Count > 0
                    && TokenEstimator.Estimate(this.history) > options.AutoCompactTokenThreshold)
                {
                    await this.CompactHistoryAsync(client, options.Model, cancellationToken).ConfigureAwait(false);
                }

                snapshot = this.history.Count;
                this.history.Add(new ChatMessage(ChatRole.User, userContent));

                await loop.RunAsync(this.history, recording, cancellationToken).ConfigureAwait(false);
            }

            await this.PersistTranscriptAsync(cancellationToken).ConfigureAwait(false);
            await this.PersistAuditTurnAsync(options, recording, loopSpec.Options.SystemPrompt, loopSpec.Tools.Definitions, cancellationToken).ConfigureAwait(false);
            this.sessionUsage = this.sessionUsage.Add(recording.Usage);
            return new RunResult(true, recording.FinalText, recording.ToolCalls, recording.StopReason, null)
            {
                Usage = recording.Usage,
                Goal = loop.LastGoalStatus,
            };
        }
        catch (OperationCanceledException)
        {
            this.Rollback(snapshot);
            return new RunResult(false, recording.FinalText, recording.ToolCalls, null, "Canceled.")
            {
                Usage = recording.Usage,
                Goal = loop.LastGoalStatus,
            };
        }
        catch (Exception ex)
        {
            this.Rollback(snapshot);
            this.LogTurnFailed(options.ProviderId, options.Model, ex.GetType().Name, ex.Message);
            return new RunResult(false, recording.FinalText, recording.ToolCalls, null, ex.Message)
            {
                Usage = recording.Usage,
                Goal = loop.LastGoalStatus,
            };
        }
        finally
        {
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

        await this.CompactHistoryAsync(client, options.Model, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The nominal context window used for the /context breakdown and percentage.</summary>
    public const int ContextWindowTokens = ContextWindow.DefaultTokens;

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

        var includeAnthropicSystemPrefix = options.ProviderId != GitHubCopilotProvider.Id;
        var outputStyle = BuiltInOutputStyles.Resolve(options.OutputStyle);
        var systemPrompt = AgentSystemPrompt.Build(
            options.WorkingDirectory,
            includeAnthropicSystemPrefix,
            ProjectContext.Load(options.WorkingDirectory),
            outputStyle.SystemPromptSuffix);

        var allDefs = new ToolRegistry([.. BuiltInTools.All(), .. options.ExtraTools]).Definitions;
        // MCP tools are namespaced "mcp__<server>__<tool>"; everything else is built-in.
        var mcpDefs = allDefs.Where(d => d.Name.StartsWith("mcp__", StringComparison.Ordinal)).ToList();
        var builtinDefs = allDefs.Where(d => !d.Name.StartsWith("mcp__", StringComparison.Ordinal)).ToList();

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
                : 0;
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

    private async Task CompactHistoryAsync(ILlmClient client, string model, CancellationToken cancellationToken)
    {
        if (this.history.Count == 0)
        {
            return;
        }

        var service = new CompactionService(new ForkedAgentRunner(client, model));
        var compacted = await service.CompactAsync(this.history, cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(compacted, this.history))
        {
            this.history.Clear();
            this.history.AddRange(compacted);
        }
    }

    private async Task PersistTranscriptAsync(CancellationToken cancellationToken)
    {
        try
        {
            this.transcriptStore ??= new SessionTranscriptStore(
                this.Options.WorkingDirectory,
                this.loggerFactory.CreateLogger<SessionTranscriptStore>());
            await this.transcriptStore.SaveAsync(this.SessionId, this.history, cancellationToken).ConfigureAwait(false);
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
                InputTokens = recording.Usage.InputTokens,
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

    /// <summary>
    /// Asynchronously tears the session down: shuts down LSP servers (bounded by
    /// <see cref="LspDisposeTimeout"/>) without any sync-over-async
    /// blocking, then releases the owned HTTP client and logger factory. This is the path
    /// <c>coda serve</c> uses — see <c>ServeHost</c>, which awaits it from its run loop so a
    /// not-fully-disposed session never leaks across turns.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
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
