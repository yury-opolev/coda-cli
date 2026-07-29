using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Coda.Common;
using Coda.Agent.Tasks;
using Coda.Agent.Compaction;
using Coda.Agent.Goals;
using Coda.Agent.Hooks;
using Coda.Agent.Lsp;
using Coda.Agent.Permissions;
using Coda.Agent.Scheduling;
using Coda.Agent.Settings;
using Coda.Agent.ToolSearch;
using Coda.Agent.Tools;
using LlmClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Agent;

/// <summary>
/// The agentic tool-use cycle: stream an assistant turn, run any requested tools
/// (permission-gated), feed the results back, and repeat until the model stops
/// requesting tools or the iteration bound is hit. Optional <see cref="AgentHooks"/>
/// add the post-sampling observe-bus and the stop-hook step-in lever.
/// </summary>
public sealed partial class AgentLoop : IAgentLoop
{
    private readonly ILlmClient client;
    private readonly ToolRegistry tools;
    private readonly IPermissionPrompt permissions;
    private readonly AgentOptions options;
    private readonly ISubagentHost? subagents;
    private readonly AgentHooks? hooks;
    private readonly TodoStore? todos;
    private readonly ScheduledTaskStore? schedules;
    private readonly IScheduleRuntimeView? scheduleRuntime;
    private readonly IUserQuestionPrompt? userQuestion;
    private readonly UserHookRunner? userHooks;

    /// <summary>
    /// The live permission rules used to compute <c>matchedRule</c> for the
    /// <c>PermissionRequest</c> hook payload and mutated by <c>updatedPermissions</c>.
    /// </summary>
    private readonly PermissionRuleStore? permissionRules;
    private readonly IPlanApprover? planApprover;
    private readonly TaskManager? tasks;
    private readonly string? currentTaskId;
    private readonly int currentDepth;
    private readonly LspServerManager? lsp;
    private readonly LspDiagnosticRegistry? lspDiagnostics;
    private readonly ToolSearchCoordinator? toolSearch;
    private readonly GoalSupervisor? goal;
    private readonly Func<List<ChatMessage>, IAgentSink, CancellationToken, Task<bool>>? compactAsync;
    private readonly SteeringInbox? steering;
    private readonly AgentExecutionGate? gate;
    private readonly ILogger logger;
    private readonly TimeSpan toolProgressInterval;
    private readonly TimeSpan toolMaxDuration;
    private readonly TimeSpan? transportRetryDelay;
    private readonly Func<CancellationToken, Task>? persistTurn;
    private readonly ToolActivityContext initialToolActivity;

    /// <summary>
    /// How often <see cref="IAgentSink.OnToolProgress"/> pulses while a tool executes. Kept
    /// well below any orchestrator idle watchdog (the Bridge's is 300s) so a legitimately
    /// long tool never reads as hung, yet cheap enough to run for every tool call.
    /// </summary>
    internal static readonly TimeSpan DefaultToolProgressInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Last-resort wall-clock ceiling on a single tool call. Tools with their own timeout
    /// (run_command, MCP) fire that first; this bounds any tool that would otherwise block
    /// forever — the universal backstop the orchestrator watchdog can no longer provide now that
    /// the tool-progress heartbeat keeps it alive during tool execution. Generous so it never
    /// interferes with a legitimately long command; overridable via <see cref="ToolMaxSecondsEnv"/>.
    /// </summary>
    internal static readonly TimeSpan DefaultToolMaxDuration = TimeSpan.FromMinutes(30);

    /// <summary>Environment variable overriding the per-tool wall-clock ceiling (whole seconds; &lt;= 0 disables).</summary>
    internal const string ToolMaxSecondsEnv = "CODA_TOOL_MAX_SECONDS";

    internal static TimeSpan ResolveToolMaxDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var seconds))
        {
            return DefaultToolMaxDuration;
        }

        return seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
    }

    public GoalStatus? LastGoalStatus { get; private set; }

    // Option labels for the at-bound goal escalation question. Kept as constants so the
    // labels presented and the answer comparison can never drift apart.
    private const string GoalContinueOption = "Provide guidance and continue";
    private const string GoalStopOption = "Stop — goal not met";

    private readonly Func<IReadOnlySet<string>?>? grantedDirectoriesSource;

    public AgentLoop(
        ILlmClient client,
        ToolRegistry tools,
        IPermissionPrompt permissions,
        AgentOptions options,
        ISubagentHost? subagents = null,
        AgentHooks? hooks = null,
        TodoStore? todos = null,
        ScheduledTaskStore? schedules = null,
        IScheduleRuntimeView? scheduleRuntime = null,
        IUserQuestionPrompt? userQuestion = null,
        UserHookRunner? userHooks = null,
        IPlanApprover? planApprover = null,
        TaskManager? tasks = null,
        string? currentTaskId = null,
        int currentDepth = 0,
        LspServerManager? lsp = null,
        LspDiagnosticRegistry? lspDiagnostics = null,
        ToolSearchCoordinator? toolSearch = null,
        GoalSupervisor? goal = null,
        Func<List<ChatMessage>, IAgentSink, CancellationToken, Task<bool>>? compactAsync = null,
        SteeringInbox? steering = null,
        ILogger? logger = null,
        TimeSpan? toolProgressInterval = null,
        Func<CancellationToken, Task>? persistTurnAsync = null,
        TimeSpan? toolMaxDuration = null,
        TimeSpan? transportRetryDelay = null,
        AgentExecutionGate? gate = null,
        ToolActivityContext? toolActivity = null,
        PermissionRuleStore? permissionRules = null,
        Func<IReadOnlySet<string>?>? grantedDirectoriesSource = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.tools = tools ?? throw new ArgumentNullException(nameof(tools));
        this.permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.subagents = subagents;
        this.hooks = hooks;
        this.todos = todos;
        this.schedules = schedules;
        this.scheduleRuntime = scheduleRuntime;
        this.userQuestion = userQuestion;
        this.userHooks = userHooks;
        this.permissionRules = permissionRules;
        this.planApprover = planApprover;
        this.tasks = tasks;
        this.currentTaskId = currentTaskId;
        this.currentDepth = currentDepth;
        this.lsp = lsp;
        this.lspDiagnostics = lspDiagnostics;
        this.toolSearch = toolSearch;
        this.goal = goal;
        this.compactAsync = compactAsync;
        this.steering = steering;
        this.gate = gate;
        this.logger = logger ?? NullLogger.Instance;
        this.toolProgressInterval = toolProgressInterval is { } interval && interval > TimeSpan.Zero
            ? interval
            : DefaultToolProgressInterval;
        this.toolMaxDuration = toolMaxDuration is { } maxDuration && maxDuration > TimeSpan.Zero
            ? maxDuration
            : ResolveToolMaxDuration(Environment.GetEnvironmentVariable(ToolMaxSecondsEnv));
        // A test seam only: when set (incl. Zero), overrides the transport-retry backoff so tests
        // don't sleep the real 0.5s/2s ladder. Production leaves it null → the real backoff.
        this.transportRetryDelay = transportRetryDelay;
        this.persistTurn = persistTurnAsync;
        this.initialToolActivity = toolActivity ?? ToolActivityContext.CreateRoot();
        this.grantedDirectoriesSource = grantedDirectoriesSource;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "turn start: iteration={iteration}, model={model}, historyMessages={messageCount}, tools={toolCount}")]
    private partial void LogTurnStart(int iteration, string model, int messageCount, int toolCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "turn end: iteration={iteration}, stop={stopReason}, toolCalls={toolCount}, textChars={textLength}")]
    private partial void LogTurnEnd(int iteration, string stopReason, int toolCount, int textLength);

    [LoggerMessage(Level = LogLevel.Debug, Message = "cache: system prompt prefix changed from previous turn; reason={reason}")]
    private partial void LogCachePrefixChanged(string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "cache: turn={iteration} read={readTokens} write={writeTokens} hitRate={hitRate:P1}")]
    private partial void LogCacheTurnStats(int iteration, int readTokens, int writeTokens, double hitRate);

    // Log the ACTUAL command each tool call carries (secrets redacted) at Information so the
    // telemetry file shows what a session was doing — even one later killed mid-tool. Without
    // this the log only records aggregate "toolCalls=N" and the command is unrecoverable.
    [LoggerMessage(Level = LogLevel.Information, Message = "tool call: {toolName} {argsSummary}")]
    private partial void LogToolCall(string toolName, string argsSummary);

    [LoggerMessage(Level = LogLevel.Debug, Message = "tool result: {toolName} isError={isError} chars={chars}")]
    private partial void LogToolResult(string toolName, bool isError, int chars);

    [LoggerMessage(Level = LogLevel.Debug, Message = "incremental transcript persist failed (best-effort); continuing the turn")]
    private partial void LogPersistTurnFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "in-loop compaction failed (best-effort); continuing the run: iteration={iteration}")]
    private partial void LogCompactionFailed(int iteration, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "context overflow on iteration={iteration}; compacting history and retrying the turn")]
    private partial void LogContextOverflowCompaction(int iteration, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "transient transport error before first content (iteration={iteration}); retrying turn (attempt {attempt})")]
    private partial void LogTransportRetry(int iteration, int attempt, Exception ex);

    /// <summary>
    /// Whether an exception from the LLM call signals the request was too long for the model's
    /// context window — the request fails identically on retry unless the history is shrunk.
    /// </summary>
    private static bool IsContextOverflowError(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("context window exceeded", StringComparison.OrdinalIgnoreCase)
            || message.Contains("context_length", StringComparison.OrdinalIgnoreCase)
            || message.Contains("maximum context", StringComparison.OrdinalIgnoreCase)
            || message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase)
            || message.Contains("too many tokens", StringComparison.OrdinalIgnoreCase)
            || message.Contains("input length and `max_tokens` exceed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether an exception is a transient transport-layer failure (a dropped/reset stream),
    /// safe to retry ONLY when no content has been emitted yet (checked at the call site).
    /// <para>
    /// <see cref="System.Net.Http.HttpRequestException"/> is intentionally NOT matched at the top
    /// level: a send-phase HttpRequestException is already retried by the headers-phase policy
    /// (see <c>LlmErrorClassifier</c>), so matching it here would re-retry permanent failures
    /// (connection refused / DNS / auth-token refresh) the policy already owns. The one-level
    /// InnerException unwrap still catches a mid-stream reset wrapped in an HttpRequestException.
    /// </para>
    /// Excludes provider status errors (LlmClientException) and timeouts (LlmHttpTimeoutException —
    /// already clean, resumable failures), and context overflow (handled separately).
    /// </summary>
    private static bool IsTransientTransportError(Exception ex)
    {
        return ex is System.IO.IOException
            or System.Net.Sockets.SocketException
            || ex.InnerException is System.IO.IOException or System.Net.Sockets.SocketException;
    }

    // Bounded pre-content retry of a turn on a transient transport failure (e.g. the provider
    // forcibly closed the connection before the first token): 2 retries, 0.5s then 2s backoff.
    private const int MaxTransportRetries = 2;

    private TimeSpan TransportRetryBackoff(int attempt) =>
        this.transportRetryDelay ?? TimeSpan.FromMilliseconds(attempt <= 1 ? 500 : 2000);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stop user hooks failed (best-effort); completing the turn")]
    private partial void LogStopHooksFailed(Exception ex);

    /// <summary>
    /// Classifies why the resolved system prompt changed from the previous turn so the log entry
    /// is actionable. If the shape has an <c>AppendSystemPrompt</c>, that is the most likely
    /// volatile cause; a full <c>SystemPrompt</c> replacement is the next; otherwise a session-
    /// level change (e.g. <c>/output-style</c> or <c>/cwd</c>) is reported.
    /// </summary>
    private static string DeterminePromptChangeReason(TurnShape? shape) =>
        shape?.AppendSystemPrompt is not null ? "append" :
        shape?.SystemPrompt is not null ? "replace" :
        "session";

    [LoggerMessage(Level = LogLevel.Information, Message = "Skill shape delta applied: model={Model}, effort={Effort}")]
    private partial void LogSkillShapeDeltaApplied(string model, string? effort);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AgentResponse hooks failed (fail-open); response passes through unchanged")]
    private partial void LogAgentResponseHooksFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "draining post-sampling hook tasks faulted (best-effort); turn already complete")]
    private partial void LogPostSamplingDrainFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "PostToolUse user hooks failed (best-effort); continuing: tool={toolName}")]
    private partial void LogPostToolUseHooksFailed(string toolName, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PermissionRequest user hooks failed; denying (fail-closed): tool={toolName}")]
    private partial void LogPermissionRequestHooksFailed(string toolName, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "run aborted by a hook returning continue:false: {reason}")]
    private partial void LogRunAbortedByHook(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PermissionRequest hook returned an unknown permission mode '{mode}' — ignoring")]
    private partial void LogUnknownPermissionMode(string mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PermissionRequest hook '{hookCommand}' requested bypassPermissions — refusing hook-driven bypass escalation")]
    private partial void LogHookBypassEscalationRefused(string hookCommand);

    [LoggerMessage(Level = LogLevel.Warning, Message = "failed to apply updatedPermissions for scope '{scope}'; the turn continues")]
    private partial void LogPermissionUpdateFailed(string scope, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PermissionRequest hook '{hookCommand}' is project-scoped but requested user-scope persistence — refusing scope escalation; clamped to project scope")]
    private partial void LogHookScopeEscalationRefused(string hookCommand);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PermissionRequest hook '{hookCommand}' supplied an over-broad allow rule '{rule}' — a bare tool name with no argument restriction would disable all approval prompts for that tool; rule was not applied")]
    private partial void LogHookOverbreadAllowRuleRefused(string hookCommand, string rule);

    [LoggerMessage(Level = LogLevel.Debug, Message = "PermissionRequest hook skipped for tool '{toolName}': deny rule '{rule}' matches — the hook is not consulted when a configured rule already blocks the call")]
    private partial void LogDenyRuleEnforcedBeforeHook(string toolName, string rule);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LSP edit-seam notify failed (best-effort); tool result and turn unaffected")]
    private partial void LogLspNotifyFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ShapeDelta from non-skill tool '{toolName}' was ignored; only ISkillShapeDeltaSource tools may modify turn shape")]
    private partial void LogShapeDeltaIgnored(string toolName);

    public async Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default, TurnShape? shape = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(sink);

        // Resolve per-turn overrides once at the start of the run. All iterations use the
        // same resolution: model, effort, system prompt, and tool filter are stable for the
        // lifetime of the call unless a skill tool applies a shape delta mid-turn.
        // (Tool-search output is still recomputed per iteration; the
        // shape filter is applied to it via resolution.FilterDefinitions.)
        var effectiveShape = shape;
        var resolution = TurnShapeResolver.Resolve(
            this.options.SystemPrompt,
            this.options.Model,
            this.options.Effort,
            this.tools,
            shape);

        // Detect when the resolved system prompt changed from the previous turn so the user
        // (and a developer reading telemetry) can tell the prompt-cache prefix shifted.
        // A changed prefix means the model will write a fresh cache entry instead of reading one.
        if (this.options.PreviousSystemPrompt is { } prevPrompt
            && !string.Equals(resolution.SystemPrompt, prevPrompt, StringComparison.Ordinal))
        {
            var reason = DeterminePromptChangeReason(shape);
            this.LogCachePrefixChanged(reason);
        }

        var pendingHookTasks = new List<Task>();
        var stopContinuations = 0;
        var stopHookActive = false;
        string? lastInjectedReminder = null;
        var activity = this.initialToolActivity;
        // Tracks the token count at which a PreCompact hook last blocked compaction. When set,
        // in-loop and overflow-path compaction are suppressed until history has grown by at least
        // one full threshold past that point — honouring the documented contract in
        // PreCompactResult ("the caller must not retry immediately") and preventing the livelock
        // where a blocking hook is re-spawned on every goal-run iteration.
        int? blockedCompactionAt = null;

        try
        {
            for (var iteration = 0; ; iteration++)
            {
                // COOPERATIVE PAUSE BOUNDARY: the first statement of every iteration, before any
                // model or tool work. When an execution gate is wired and a pause is active, park
                // here until every pause lease is released; otherwise this returns immediately.
                if (this.gate is not null)
                {
                    await this.gate.WaitIfPaused(cancellationToken).ConfigureAwait(false);
                }

                // When no goal is active, honour the MaxIterations bound exactly as before.
                if (this.goal is null && iteration >= this.options.MaxIterations)
                {
                    break;
                }

                // Goal runs: the budget governs termination, not MaxIterations.
                // In-loop compaction keeps long runs within the context window.
                if (this.goal is not null
                    && this.compactAsync is not null
                    && this.options.AutoCompact
                    && this.options.AutoCompactTokenThreshold > 0)
                {
                    var currentTokens = TokenEstimator.Estimate(history);
                    var growthBuffer = this.options.AutoCompactTokenThreshold;
                    var suppressedByBlock = blockedCompactionAt is not null
                        && currentTokens <= blockedCompactionAt.Value + growthBuffer;

                    if (!suppressedByBlock && currentTokens > this.options.AutoCompactTokenThreshold)
                    {
                        try
                        {
                            var didCompact = await this.compactAsync(history, sink, cancellationToken).ConfigureAwait(false);
                            if (!didCompact)
                            {
                                blockedCompactionAt = currentTokens;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // Compaction is best-effort; never aborts the run.
                            this.LogCompactionFailed(iteration, ex);
                        }
                    }
                }

                // DIAGNOSTICS SURFACING SEAM: after at least one tool cycle, check for
                // fresh LSP diagnostics and inject them as a synthetic user message so
                // the model sees compiler results on its edits. This runs before each
                // model call except the very first (iteration == 0 means no tool cycle yet).
                // Give async notifications a brief chance to arrive (up to ~300 ms polling).
                if (iteration > 0 && this.lspDiagnostics is not null)
                {
                    await WaitForDiagnosticsAsync(this.lspDiagnostics, cancellationToken).ConfigureAwait(false);
                    var diags = this.lspDiagnostics.CheckForDiagnostics();
                    if (diags.Count > 0)
                    {
                        var formatted = FormatDiagnostics(diags, this.options.WorkingDirectory);
                        history.Add(new ChatMessage(ChatRole.User, [new TextBlock(formatted)]));
                    }
                }

                // STEERING INBOX SEAM: drain operator steering comments posted mid-turn (via the
                // serve `session/steer` request) and inject them as a synthetic user message before
                // the next model call, so a running turn can be redirected. Mirrors the LSP diagnostics
                // seam; runs every iteration so a steer is honored at the next iteration boundary.
                if (this.steering is not null)
                {
                    var steers = this.steering.TakeAllForDelivery();
                    if (steers.Count > 0)
                    {
                        var steerText = string.Join("\n\n", steers.Select(entry => entry.Text));
                        history.Add(new ChatMessage(ChatRole.User, [new TextBlock(steerText)]));
                        sink.OnSteeringDelivered(steers.Select(entry => entry.Id).ToArray());
                    }
                }

                // DEFERRED-TOOLS REMINDER SEAM: when tool search is active, inject a
                // <deferred-tools> reminder block before each model request so the model
                // knows which tools exist but whose schemas are not yet loaded. We only
                // append when the reminder text changes (or is first injected) to avoid
                // re-injecting an identical block every turn. Mirrors the LSP seam.
                if (this.toolSearch is not null && this.toolSearch.IsActive)
                {
                    var reminder = this.toolSearch.BuildDeferredToolsReminder(this.tools);
                    if (reminder is not null && !string.Equals(reminder, lastInjectedReminder, StringComparison.Ordinal))
                    {
                        history.Add(new ChatMessage(ChatRole.User, [new TextBlock(reminder)]));
                        lastInjectedReminder = reminder;
                    }
                }

                // Per-request wire tool definitions: when tool search is active, the
                // discovered set may grow during the turn, so we recompute each call.
                // When inactive (or no coordinator), use the resolver's pre-computed definitions.
                // Apply shape filtering after whichever branch produced the definitions.
                IReadOnlyList<ToolDefinition> toolDefinitions;
                if (this.toolSearch is not null && this.toolSearch.IsActive)
                {
                    toolDefinitions = resolution.FilterDefinitions(this.toolSearch.BuildWireDefinitions(this.tools));
                }
                else
                {
                    toolDefinitions = resolution.ToolDefinitions;
                }

                var request = new ChatRequest
                {
                    Model = resolution.Model,
                    MaxTokens = this.options.MaxTokens,
                    System = resolution.SystemPrompt,
                    Messages = history,
                    Tools = toolDefinitions,
                    Effort = resolution.Effort,
                    ToolsVolatile = this.toolSearch is not null && this.toolSearch.IsActive,
                    ToolChoice = resolution.ToolChoice,
                    UseOnehourTtl = this.options.UseOnehourTtl,
                };

                var text = new StringBuilder();
                var toolUses = new List<ToolUseBlock>();
                var thinkingBlocks = new List<ThinkingBlock>();
                var redactedThinkingBlocks = new List<RedactedThinkingBlock>();
                string? stopReason = null;
                var thinkingBurstOpen = false;
                TokenUsage? capturedUsage = null;
                var iterationStartTick = Stopwatch.GetTimestamp();

                this.LogTurnStart(iteration, resolution.Model, history.Count, toolDefinitions.Count);

                // Reactive overflow compaction: if the provider rejects the request because the
                // context is too long, summarize the history once and retry the turn — rather than
                // failing the run. (Proactive window-relative compaction usually prevents this; this
                // is the safety net for a single oversized turn.)
                var overflowRetried = false;
                var transportRetries = 0;
                while (true)
                {
                    try
                    {
                        await foreach (var streamEvent in this.client.StreamAsync(request, cancellationToken).ConfigureAwait(false))
                        {
                            switch (streamEvent.Kind)
                            {
                                case AssistantEventKind.TextDelta:
                                    text.Append(streamEvent.Text);
                                    sink.OnAssistantText(streamEvent.Text!);
                                    break;

                                case AssistantEventKind.ToolUse:
                                    toolUses.Add(streamEvent.ToolUse!);
                                    break;

                                case AssistantEventKind.Done:
                                    stopReason = streamEvent.StopReason;
                                    if (streamEvent.Usage is { } turnUsage)
                                    {
                                        sink.OnUsage(turnUsage);
                                        capturedUsage = turnUsage;

                                        // Log cache hit rate only when the turn has cache activity.
                                        if (turnUsage.HasCacheActivity)
                                        {
                                            var hitRate = turnUsage.TotalInputTokens > 0
                                                ? (double)turnUsage.CacheReadTokens / turnUsage.TotalInputTokens
                                                : 0.0;
                                            this.LogCacheTurnStats(
                                                iteration,
                                                turnUsage.CacheReadTokens,
                                                turnUsage.CacheWriteTokens,
                                                hitRate);
                                        }
                                    }

                                    break;

                                case AssistantEventKind.ThinkingDelta:
                                    thinkingBurstOpen = true;
                                    sink.OnThinking(streamEvent.Text!);
                                    break;

                                case AssistantEventKind.ThinkingComplete:
                                    if (streamEvent.RedactedThinking is { } redactedBlock)
                                    {
                                        // Opaque redacted block: no user-visible burst, just preserve for replay.
                                        redactedThinkingBlocks.Add(redactedBlock);
                                    }
                                    else
                                    {
                                        thinkingBurstOpen = false;
                                        sink.OnThinkingComplete(streamEvent.ThinkingTokens);
                                        if (streamEvent.Thinking is { } completedBlock)
                                        {
                                            thinkingBlocks.Add(completedBlock);
                                        }
                                    }

                                    break;
                            }
                        }

                        break;
                    }
                    catch (Exception ex) when (!overflowRetried
                        && this.compactAsync is not null
                        && ex is not OperationCanceledException
                        && IsContextOverflowError(ex))
                    {
                        var currentTokens = TokenEstimator.Estimate(history);
                        var growthBuffer = this.options.AutoCompactTokenThreshold;
                        var suppressedByBlock = blockedCompactionAt is not null
                            && currentTokens <= blockedCompactionAt.Value + growthBuffer;

                        // If a previous PreCompact block is still suppressing this path, treat
                        // the overflow as unrecoverable for this iteration (rethrow so the turn
                        // surfaces an error rather than spawning the hook subprocess again).
                        if (suppressedByBlock)
                        {
                            throw;
                        }

                        overflowRetried = true;
                        this.LogContextOverflowCompaction(iteration, ex);

                        // Discard the partial turn and summarize the history in place, then retry.
                        text.Clear();
                        toolUses.Clear();
                        thinkingBlocks.Clear();
                        redactedThinkingBlocks.Clear();
                        stopReason = null;
                        capturedUsage = null;
                        var didCompact = await this.compactAsync(history, sink, cancellationToken).ConfigureAwait(false);
                        if (!didCompact)
                        {
                            blockedCompactionAt = currentTokens;
                        }

                        request = request with { Messages = history };
                    }
                    catch (Exception ex) when (transportRetries < MaxTransportRetries
                        && !cancellationToken.IsCancellationRequested
                        && ex is not OperationCanceledException
                        && text.Length == 0 && toolUses.Count == 0 && stopReason is null
                        && IsTransientTransportError(ex))
                    {
                        // A transport-level failure (e.g. the provider forcibly closed the connection)
                        // BEFORE anything reached the sink. The guard is airtight: no text/tool-use was
                        // yielded, and stopReason is null so no terminal Done event fired (which would
                        // have emitted usage) — so re-running the turn is clean: no duplicate output, no
                        // double tool execution, no double-counted usage. Once any of those is set, a
                        // mid-stream failure surfaces rather than replaying. A caller cancel is excluded
                        // too, so a cancellation that surfaces as IOException isn't spuriously retried.
                        transportRetries++;
                        this.LogTransportRetry(iteration, transportRetries, ex);
                        await Task.Delay(this.TransportRetryBackoff(transportRetries), cancellationToken).ConfigureAwait(false);
                    }
                }

                // Finalize any open thinking burst that the provider stream did not explicitly close.
                // Mirrors the unconditional OnAssistantTextComplete below: both are called at the
                // iteration boundary regardless of whether the stream emitted the closing event.
                if (thinkingBurstOpen)
                {
                    sink.OnThinkingComplete();
                    thinkingBurstOpen = false;
                }

                sink.OnAssistantTextComplete();

                this.LogTurnEnd(iteration, stopReason ?? "(none)", toolUses.Count, text.Length);

                var assistantContent = new List<ContentBlock>();
                // Redacted thinking blocks (opaque, no user-visible text) precede signed thinking
                // blocks in the assistant turn so Anthropic receives them before the tool_use items.
                foreach (var block in redactedThinkingBlocks)
                {
                    assistantContent.Add(block);
                }

                // Thinking blocks precede text and tool_use in the assistant turn so the provider
                // receives them in the same order they were emitted. Only blocks with a signature
                // are included; unsigned blocks are not replayable and are silently skipped.
                foreach (var block in thinkingBlocks)
                {
                    if (block.Signature is not null)
                    {
                        assistantContent.Add(block);
                    }
                }

                if (text.Length > 0)
                {
                    assistantContent.Add(new TextBlock(text.ToString()));
                }

                if (toolUses.Count > 0)
                {
                    activity = activity.EnsureActivity();
                    for (var index = 0; index < toolUses.Count; index++)
                    {
                        var toolUse = toolUses[index];
                        var identity = activity.ForCall(toolUse.Id);
                        toolUses[index] = toolUse with
                        {
                            RootTurnId = identity.RootTurnId,
                            ActivityId = identity.ActivityId,
                            SourceId = identity.SourceId,
                        };
                    }
                }

                assistantContent.AddRange(toolUses);
                history.Add(new ChatMessage(ChatRole.Assistant, assistantContent));

                // Record on the go: persist the transcript the moment the assistant turn (with
                // its tool_use blocks — the requested commands) is committed to history, so a
                // kill during the ensuing tool execution still leaves a record of what it asked.
                await this.MaybePersistTurnAsync(cancellationToken).ConfigureAwait(false);

                // Observe-bus: fire post-sampling hooks after each assistant turn
                // (non-blocking; drained in the finally below).
                if (this.hooks is not null)
                {
                    pendingHookTasks.AddRange(this.hooks.FirePostSampling(this.BuildHookContext(history), cancellationToken));
                }

                // The API sets stop_reason="tool_use" whenever tool_use blocks are
                // present, so drive off the presence of tool calls.
                if (toolUses.Count == 0)
                {
                    if (this.goal is not null)
                    {
                        // Goal path: consult the supervisor before generic stop hooks.
                        // The goal path and the generic stop-hook path are mutually exclusive.
                        var verdict = await this.goal
                            .EvaluateAsync(this.BuildHookContext(history), cancellationToken)
                            .ConfigureAwait(false);

                        switch (verdict)
                        {
                            case GoalVerdict.Continue c:
                                history.Add(new ChatMessage(ChatRole.User, [new TextBlock(c.Nudge)]));
                                continue;

                            case GoalVerdict.Escalate e:
                                var answer = this.userQuestion is null
                                    ? null
                                    : await this.userQuestion
                                        .AskAsync(
                                            e.Question,
                                            [GoalContinueOption, GoalStopOption],
                                            false,
                                            cancellationToken)
                                        .ConfigureAwait(false);

                                // Any non-empty answer that is not an explicit stop is treated as
                                // "continue with this guidance". A null answer means no interactive
                                // user (headless) — stop with the goal unmet.
                                var wantsContinue = !string.IsNullOrWhiteSpace(answer)
                                    && !string.Equals(answer, GoalStopOption, StringComparison.OrdinalIgnoreCase);

                                if (wantsContinue)
                                {
                                    if (this.goal.TryGrantExtension())
                                    {
                                        history.Add(new ChatMessage(ChatRole.User,
                                            [new TextBlock($"Operator guidance: {answer}\nContinue toward the goal.")]));
                                        continue;
                                    }

                                    // The single bounded extension was already spent — surface why we stop.
                                    sink.OnError("The budget extension was already used; stopping with the goal unmet.");
                                }

                                this.goal.MarkStoppedUnmet();
                                break;

                            case GoalVerdict.Stop:
                                break;
                        }

                        this.LastGoalStatus = this.goal.Status;
                        // Fall through to the normal stop completion below.
                    }
                    else if (this.hooks is { } activeHooks
                        && activeHooks.HasStopHooks
                        && stopContinuations < this.options.MaxStopContinuations)
                    {
                        // Generic stop-hook path (only when no goal is active).
                        var outcome = await activeHooks
                            .RunStopHooksAsync(this.BuildHookContext(history), stopHookActive, cancellationToken)
                            .ConfigureAwait(false);

                        if (outcome.ShouldContinue)
                        {
                            history.Add(new ChatMessage(ChatRole.User, [new TextBlock(outcome.InjectedMessage)]));
                            stopHookActive = true;
                            stopContinuations++;
                            continue;
                        }
                    }

                    // Shell Stop hooks with full blocking power — share the same stopContinuations
                    // counter as the in-process IStopHook path so neither can each spend the full budget.
                    if (this.userHooks is { HasStop: true }
                        && stopContinuations < this.options.MaxStopContinuations)
                    {
                        StopHookOutcome shellOutcome;
                        try
                        {
                            shellOutcome = await this.userHooks.RunStopWithOutcomeAsync(
                                stopReason, iteration, stopContinuations, stopHookActive,
                                cancellationToken, this.currentDepth, this.currentTaskId).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // Fail-open: a broken stop hook must not trap the agent in a loop.
                            this.LogStopHooksFailed(ex);
                            shellOutcome = StopHookOutcome.Stop;
                        }

                        if (shellOutcome.ShouldContinue)
                        {
                            history.Add(new ChatMessage(ChatRole.User, [new TextBlock(shellOutcome.InjectedMessage)]));
                            stopHookActive = true;
                            stopContinuations++;
                            continue;
                        }
                    }

                    // Seal only at a natural completion. A failed seal means an operator raced the
                    // boundary; loop once more to deliver it before asking the model again.
                    if (this.steering is not null && !this.steering.TrySealEmpty())
                    {
                        continue;
                    }

                    // AgentResponse hooks: run after stop hooks agreed to stop, before display and
                    // persistence. The final assistant text is now settled. Fires on every turn —
                    // including a tool-only turn whose final text is empty — because this is an
                    // audit surface and a silent turn is exactly what an auditor needs to see.
                    // Fail-open: a broken or timed-out hook leaves the response completely unchanged.
                    if (this.userHooks is { HasAgentResponse: true })
                    {
                        var responseText = text.ToString();
                        var durationMs = (long)(Stopwatch.GetElapsedTime(iterationStartTick).TotalMilliseconds);
                        AgentResponseResult agentResponseResult;
                        try
                        {
                            agentResponseResult = await this.userHooks.RunAgentResponseAsync(
                                responseText, stopReason, capturedUsage ?? TokenUsage.Zero, durationMs,
                                cancellationToken, this.currentDepth, this.currentTaskId).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // Fail-open: an exception from RunAgentResponseAsync leaves the response unchanged.
                            this.LogAgentResponseHooksFailed(ex);
                            agentResponseResult = AgentResponseResult.NoChange;
                        }

                        if (agentResponseResult.HasChange)
                        {
                            // When only displayContent is set, the user also sees modifiedResponse would
                            // be redundant — fall back to displayContent. When only modifiedResponse is set,
                            // the user sees that too (display and history both get modifiedResponse).
                            var displayContent = agentResponseResult.DisplayContent
                                ?? agentResponseResult.ModifiedResponse!;

                            if (agentResponseResult.ModifiedResponse is not null)
                            {
                                ReplaceLastAssistantText(history, agentResponseResult.ModifiedResponse);
                            }

                            sink.OnResponseRewritten(
                                agentResponseResult.ByHookCommand!,
                                responseText,
                                displayContent,
                                agentResponseResult.ModifiedResponse);
                        }
                    }

                    sink.OnStopReason(stopReason);
                    if (stopReason == "max_tokens")
                    {
                        sink.OnLimitReached("max_tokens", "The response was truncated (max_tokens reached).");
                    }

                    return; // turn complete
                }

                // A tool cycle intervened, so any subsequent stop is a fresh one — not a
                // direct result of a prior stop-hook continuation. Reset so stop hooks
                // treat the next natural stop correctly.
                stopHookActive = false;
                var (resultBlocks, shapeDelta, abortReason) = await this.RunToolsAsync(toolUses, activity, sink, resolution, cancellationToken).ConfigureAwait(false);
                history.Add(new ChatMessage(ChatRole.User, resultBlocks));

                // A PreToolUse hook returned continue:false — the protocol's hard stop. Persist
                // what has happened so far and end the run without sampling the model again.
                if (abortReason is not null)
                {
                    await this.MaybePersistTurnAsync(cancellationToken).ConfigureAwait(false);
                    this.LogRunAbortedByHook(abortReason);
                    sink.OnStopReason("hook_abort");
                    sink.OnLimitReached("hook_abort", $"A hook stopped the run: {abortReason}");
                    return;
                }

                // When a skill tool returned a shape delta, layer it onto the effective shape and
                // re-resolve so subsequent iterations see the updated model/effort/tool restrictions.
                if (shapeDelta is not null)
                {
                    effectiveShape = TurnShape.Layer(effectiveShape, shapeDelta);
                    resolution = TurnShapeResolver.Resolve(
                        this.options.SystemPrompt,
                        this.options.Model,
                        this.options.Effort,
                        this.tools,
                        effectiveShape);
                    this.LogSkillShapeDeltaApplied(resolution.Model, resolution.Effort);
                }

                // Persist again once tool results are in history, so a kill in the gap before
                // the next sampling still captures the outputs, not just the requests.
                await this.MaybePersistTurnAsync(cancellationToken).ConfigureAwait(false);
            }

            // Only the non-goal path breaks out of the loop via the MaxIterations bound.
            // Keep history valid (ending on an assistant turn) even when we bail out.
            history.Add(new ChatMessage(ChatRole.Assistant, [new TextBlock("(stopped: reached the maximum tool iterations)")]));
            sink.OnLimitReached("max_tool_iterations", $"Reached the maximum of {this.options.MaxIterations} tool iterations.");
        }
        finally
        {
            // Drain background watchers so their work completes deterministically.
            if (pendingHookTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(pendingHookTasks).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Individual hook failures are already swallowed in FirePostSampling.
                    this.LogPostSamplingDrainFailed(ex);
                }
            }
        }
    }

    private ReplHookContext BuildHookContext(List<ChatMessage> history) => new()
    {
        Messages = history.ToArray(),
        SystemPrompt = this.options.SystemPrompt,
        WorkingDirectory = this.options.WorkingDirectory,
    };

    /// <summary>
    /// Replaces the <see cref="TextBlock"/> of the last assistant message in <paramref name="history"/>
    /// with <paramref name="newText"/>. Called by the <c>AgentResponse</c> hook path when
    /// <c>modifiedResponse</c> is set to keep history consistent with what was displayed.
    /// </summary>
    private static void ReplaceLastAssistantText(List<ChatMessage> history, string newText)
    {
        if (history.Count == 0)
        {
            return;
        }

        var last = history[history.Count - 1];
        if (last.Role != ChatRole.Assistant)
        {
            return;
        }

        var replaced = false;
        var newContent = new List<ContentBlock>(last.Content.Count);
        foreach (var block in last.Content)
        {
            if (!replaced && block is TextBlock)
            {
                newContent.Add(new TextBlock(newText));
                replaced = true;
            }
            else
            {
                newContent.Add(block);
            }
        }

        if (replaced)
        {
            history[history.Count - 1] = new ChatMessage(ChatRole.Assistant, newContent);
        }
    }

    private async Task<(List<ContentBlock> Blocks, TurnShape? ShapeDelta, string? AbortReason)> RunToolsAsync(
        IReadOnlyList<ToolUseBlock> toolUses,
        ToolActivityContext activity,
        IAgentSink sink,
        TurnShapeResolution resolution,
        CancellationToken cancellationToken)
    {
        var results = new List<ContentBlock>();
        TurnShape? accumulatedDelta = null;
        string? abortReason = null;
        var identities = toolUses.Select(toolUse => activity.ForCall(toolUse.Id)).ToArray();
        var context = new ToolContext(this.options.WorkingDirectory)
        {
            Sink = sink,
            ToolActivity = activity,
            Subagents = this.subagents,
            Todos = this.todos,
            Schedules = this.schedules,
            ScheduleRuntime = this.scheduleRuntime,
            UserQuestion = this.userQuestion,
            PlanApprover = this.planApprover,
            Tasks = this.tasks,
            CurrentTaskId = this.currentTaskId,
            CurrentDepth = this.currentDepth,
            Lsp = this.lsp,
            AllTools = this.tools.All,
            OnToolsDiscovered = names => this.toolSearch?.AddDiscovered(names),
            Logger = this.logger,
            ParentToolRestriction = resolution.ToToolRestrictionShape(),
            GrantedDirectories = this.grantedDirectoriesSource?.Invoke(),
        };

        for (var i = 0; i < toolUses.Count; i++)
        {
            var toolUse = toolUses[i];
            sink.OnToolQueued(identities[i], toolUse.Name, toolUse.InputJson);
        }

        for (var i = 0; i < toolUses.Count; i++)
        {
            var toolUse = toolUses[i];
            var identity = identities[i];
            var delivered = this.steering?.TakeAllForDelivery() ?? [];
            if (delivered.Count > 0)
            {
                for (var skippedIndex = i; skippedIndex < toolUses.Count; skippedIndex++)
                {
                    var skipped = toolUses[skippedIndex];
                    var skippedIdentity = identities[skippedIndex];
                    var skippedResult = new ToolResult(
                        "Skipped: not executed because new operator steering arrived before this tool started.",
                        IsError: true);
                    sink.OnToolResult(skippedIdentity, skipped.Name, skippedResult, ToolCallStatus.Skipped);
                    results.Add(CreateToolResultBlock(skippedIdentity, skippedResult, ToolCallStatus.Skipped));
                }

                results.Add(new TextBlock(string.Join("\n\n", delivered.Select(entry => entry.Text))));
                sink.OnSteeringDelivered(delivered.Select(entry => entry.Id).ToArray());
                break;
            }

            var effectiveInput = toolUse.InputJson;

            var tool = this.tools.Resolve(toolUse.Name);
            if (tool is null)
            {
                sink.OnToolCall(identity, toolUse.Name, effectiveInput);
                this.LogToolCall(toolUse.Name, SummarizeToolInput(effectiveInput));
                var unknown = new ToolResult($"Unknown tool '{toolUse.Name}'.", IsError: true);
                sink.OnToolResult(identity, toolUse.Name, unknown, ToolCallStatus.Failed);
                results.Add(CreateToolResultBlock(identity, unknown, ToolCallStatus.Failed));
                continue;
            }

            // Enforce turn-shape tool restriction. Advertising a filtered set but executing
            // from the unfiltered registry would make the restriction cosmetic — per proposal
            // §8, tool filtering is a policy mechanism and must be enforced at invocation too.
            if (!resolution.IsToolAllowed(toolUse.Name))
            {
                sink.OnToolCall(identity, toolUse.Name, effectiveInput);
                this.LogToolCall(toolUse.Name, SummarizeToolInput(effectiveInput));
                var denied = new ToolResult(
                    $"Tool '{toolUse.Name}' is not available this turn.",
                    IsError: true);
                sink.OnToolResult(identity, toolUse.Name, denied, ToolCallStatus.Failed);
                results.Add(CreateToolResultBlock(identity, denied, ToolCallStatus.Failed));
                continue;
            }

            // Check user PreToolUse hooks BEFORE the permission prompt so a hook can
            // block a call even when permissions would otherwise allow it. The hook may also
            // replace the arguments outright (hookSpecificOutput.modifiedInput), so it runs
            // before OnToolCall — tool activity must report what the tool actually ran with.
            string? inputModifiedBy = null;
            if (this.userHooks is not null && this.userHooks.HasPreToolUse)
            {
                var hookResult = await this.userHooks
                    .RunPreToolUseAsync(toolUse.Name, effectiveInput, cancellationToken, this.currentDepth, this.currentTaskId)
                    .ConfigureAwait(false);

                if (hookResult.Block)
                {
                    sink.OnToolCall(identity, toolUse.Name, effectiveInput);
                    this.LogToolCall(toolUse.Name, SummarizeToolInput(effectiveInput));
                    var blocked = new ToolResult(
                        $"Blocked by hook: {hookResult.Message}",
                        IsError: true);
                    sink.OnToolResult(identity, toolUse.Name, blocked, ToolCallStatus.Failed);
                    results.Add(CreateToolResultBlock(identity, blocked, ToolCallStatus.Failed));

                    // continue:false is the protocol's hard stop. Record the blocked call, then
                    // skip the rest of the batch — the run ends instead of handing the block back
                    // to the model for another attempt.
                    if (hookResult.Abort)
                    {
                        abortReason = hookResult.Message ?? "hook requested stop";
                        for (var skippedIndex = i + 1; skippedIndex < toolUses.Count; skippedIndex++)
                        {
                            var skipped = toolUses[skippedIndex];
                            var skippedIdentity = identities[skippedIndex];
                            var skippedResult = new ToolResult(
                                "Skipped: a hook aborted the run before this tool started.",
                                IsError: true);
                            sink.OnToolResult(skippedIdentity, skipped.Name, skippedResult, ToolCallStatus.Skipped);
                            results.Add(CreateToolResultBlock(skippedIdentity, skippedResult, ToolCallStatus.Skipped));
                        }

                        break;
                    }

                    continue;
                }

                if (hookResult.ModifiedInput is { } replacement)
                {
                    inputModifiedBy = hookResult.ByHookCommand ?? string.Empty;
                    effectiveInput = replacement;
                }
            }

            sink.OnToolCall(identity, toolUse.Name, effectiveInput);
            this.LogToolCall(toolUse.Name, SummarizeToolInput(effectiveInput));

            if (inputModifiedBy is not null)
            {
                sink.OnToolInputModified(inputModifiedBy, toolUse.Name, toolUse.InputJson, effectiveInput);
            }

            if (!tool.IsReadOnly)
            {
                // HIGH-2: deny rules are a floor the hook cannot lift. Evaluate matching deny
                // rules before consulting any PermissionRequest hook — a call that would be
                // blocked by a rule would never reach the interactive prompt either, so the
                // documented "only when it would otherwise prompt" semantics mean the hook must
                // not see it at all.
                if (this.permissionRules is { } denyCheckRules)
                {
                    PermissionRule? matchedDeny = null;
                    foreach (var denyRule in denyCheckRules.Deny)
                    {
                        if (denyRule.Matches(tool.Name, effectiveInput))
                        {
                            matchedDeny = denyRule;
                            break;
                        }
                    }

                    if (matchedDeny is not null)
                    {
                        this.LogDenyRuleEnforcedBeforeHook(tool.Name, matchedDeny.ToRuleString());
                        var deniedByRule = new ToolResult(
                            $"Permission denied by rule: {matchedDeny.ToRuleString()}",
                            IsError: true);
                        sink.OnToolResult(identity, toolUse.Name, deniedByRule, ToolCallStatus.Failed);
                        results.Add(CreateToolResultBlock(identity, deniedByRule, ToolCallStatus.Failed));
                        continue; // outer for loop: skip hook, prompt, and execution for this tool
                    }
                }

                sink.OnToolStatus(identity, toolUse.Name, ToolCallStatus.AwaitingApproval);

                // Fire Notification("approval") fire-and-forget so approval-pending hooks
                // never delay the permission prompt.
                if (this.userHooks?.HasNotification == true)
                {
                    _ = this.userHooks.RunNotificationAsync(
                        "approval",
                        $"Approval pending for tool: {toolUse.Name}",
                        this.currentTaskId,
                        CancellationToken.None);
                }

                // PermissionRequest hooks see the call after PreToolUse passed and only when the
                // tool would actually prompt. They may grant, refuse, or defer to the prompt.
                var grantedByHook = false;
                if (this.userHooks is not null && this.userHooks.HasPermissionRequest)
                {
                    var decision = await this
                        .DecidePermissionByHookAsync(toolUse.Name, effectiveInput, cancellationToken)
                        .ConfigureAwait(false);

                    this.ApplyUpdatedPermissions(decision.UpdatedPermissions, decision.ByHookCommand ?? string.Empty, sink, decision.ByHookScope);

                    if (decision.ModifiedInput is { } permissionReplacement
                        && !string.Equals(permissionReplacement, effectiveInput, StringComparison.Ordinal))
                    {
                        sink.OnToolInputModified(
                            decision.ByHookCommand ?? string.Empty,
                            toolUse.Name,
                            effectiveInput,
                            permissionReplacement);
                        effectiveInput = permissionReplacement;
                    }

                    if (decision.IsDeny)
                    {
                        sink.OnPermissionDecided(decision.ByHookCommand ?? string.Empty, toolUse.Name, PermissionDecisions.Deny);
                        var refused = new ToolResult(
                            decision.Reason is { Length: > 0 } reason
                                ? $"Permission denied by hook: {reason}"
                                : "Permission denied by a PermissionRequest hook.",
                            IsError: true);
                        sink.OnToolResult(identity, toolUse.Name, refused, ToolCallStatus.Failed);
                        results.Add(CreateToolResultBlock(identity, refused, ToolCallStatus.Failed));
                        continue;
                    }

                    if (decision.IsAllow)
                    {
                        sink.OnPermissionDecided(decision.ByHookCommand ?? string.Empty, toolUse.Name, PermissionDecisions.Allow);
                        grantedByHook = true;
                    }
                }

                if (!grantedByHook)
                {
                    // Pre-approved tools (explicitly in AllowedTools when set by a skill or hook)
                    // skip the user permission prompt — the shape is the approval surface.
                    if (resolution.IsPreApprovedTool(toolUse.Name))
                    {
                        grantedByHook = true;
                    }
                    else
                    {
                        bool allowed;
                        try
                        {
                            allowed = await this.permissions
                                .RequestAsync(tool, effectiveInput, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            var promptError = new ToolResult($"Permission prompt error: {ex.Message}", IsError: true);
                            sink.OnToolResult(identity, toolUse.Name, promptError, ToolCallStatus.Failed);
                            results.Add(CreateToolResultBlock(identity, promptError, ToolCallStatus.Failed));
                            continue;
                        }

                        if (!allowed)
                        {
                            var userDenied = new ToolResult("Permission denied by the user.", IsError: true);
                            sink.OnToolResult(identity, toolUse.Name, userDenied, ToolCallStatus.Failed);
                            results.Add(CreateToolResultBlock(identity, userDenied, ToolCallStatus.Failed));
                            continue;
                        }
                    }
                }
            }

            ToolResult result;
            // Pulse a liveness heartbeat while the tool runs so the orchestrator's idle
            // watchdog can tell "a long tool is working" from "the process is wedged". The
            // pump is torn down in the finally — including on the OperationCanceledException
            // rethrow path — so it can never outlive the tool call.
            var toolStartedAt = Stopwatch.GetTimestamp();
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeat = PumpToolProgressAsync(
                sink,
                identity,
                toolUse.Name,
                this.toolProgressInterval,
                toolStartedAt,
                heartbeatCts.Token);

            // Last-resort wall-clock ceiling: the token handed to the tool is cancelled if it runs
            // past toolMaxDuration, so no single tool can wedge the session forever (the backstop the
            // watchdog no longer provides during tool execution). Tools with a shorter self-timeout
            // fire that first; a caller/turn cancel is distinguished below and still unwinds the turn.
            using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (this.toolMaxDuration != Timeout.InfiniteTimeSpan)
            {
                toolCts.CancelAfter(this.toolMaxDuration);
            }

            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(effectiveInput) ? "{}" : effectiveInput);

                // Recompute the sandbox flag per individual tool execution (not once per batch) so a
                // mid-batch mode change (Default→Bypass or back) applies to the very next tool. Read
                // the mode live from the shared state; fall back to the snapshot mode for a fixed
                // headless run with no shared state.
                var toolContext = context with
                {
                    AllowOutsideWorkingDirectory =
                        (this.options.PermissionModeState?.Mode ?? this.options.PermissionMode) == PermissionMode.BypassPermissions,
                };
                sink.OnToolStatus(identity, toolUse.Name, ToolCallStatus.Running);
                result = await tool.ExecuteAsync(doc.RootElement, toolContext, toolCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (toolCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The ceiling fired (not a caller/turn cancel) — terminate just this tool and hand the
                // model a clean error, so the session keeps running instead of wedging.
                result = new ToolResult(
                    $"Tool '{toolUse.Name}' exceeded the {this.toolMaxDuration.TotalSeconds:N0}s maximum run time and was terminated.",
                    IsError: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Tool error: {ex.Message}", IsError: true);
            }
            finally
            {
                heartbeatCts.Cancel();
                try
                {
                    await heartbeat.ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort teardown; the pump swallows its own cancellation.
                }
            }

            // Fire PostToolUse hooks. The tool has already run — a hook can only change what the
            // model is told (modifiedResult, or decision:block whose reason replaces the result).
            // Fires on the failure path too, with the failure text in the payload's `error` field.
            if (this.userHooks is not null)
            {
                try
                {
                    var post = await this.userHooks
                        .RunPostToolUseAsync(
                            toolUse.Name,
                            effectiveInput,
                            result.Content,
                            cancellationToken,
                            this.currentDepth,
                            this.currentTaskId,
                            errorText: result.IsError ? result.Content : null)
                        .ConfigureAwait(false);

                    if (post.Block && post.Reason is { Length: > 0 } blockReason)
                    {
                        sink.OnToolResultModified(post.ByHookCommand ?? string.Empty, toolUse.Name, result.Content, blockReason);
                        result = new ToolResult(blockReason, IsError: true);
                    }
                    else if (post.ModifiedResult is { } replacementResult)
                    {
                        sink.OnToolResultModified(post.ByHookCommand ?? string.Empty, toolUse.Name, result.Content, replacementResult);
                        result = result with { Content = replacementResult };
                    }
                }
                catch (Exception ex)
                {
                    // User hook errors must not interrupt normal turn completion.
                    this.LogPostToolUseHooksFailed(toolUse.Name, ex);
                }
            }

            var terminalStatus = result.IsError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded;
            sink.OnToolResult(identity, toolUse.Name, result, terminalStatus);
            this.LogToolResult(toolUse.Name, result.IsError, result.Content.Length);
            results.Add(CreateToolResultBlock(identity, result, terminalStatus));

            // Accumulate shape delta only from tools that implement ISkillShapeDeltaSource.
            // Accepting deltas from arbitrary tools would let any in-process tool pre-approve
            // itself or others by injecting a PreApprovedTools list — I3 gate.
            if (result.ShapeDelta is { } delta)
            {
                if (tool is ISkillShapeDeltaSource)
                {
                    accumulatedDelta = TurnShape.Layer(accumulatedDelta, delta);
                }
                else
                {
                    this.LogShapeDeltaIgnored(toolUse.Name);
                }
            }

            // EDIT SEAM: when a mutating file tool succeeds, notify the LSP server
            // about the new file content (change + save) so it can publish diagnostics.
            // Failures are swallowed — LSP must never break a tool result.
            if (!result.IsError && this.lsp is not null && IsMutatingFileTool(toolUse.Name))
            {
                await this.NotifyLspFileEditedAsync(effectiveInput, cancellationToken).ConfigureAwait(false);
            }
        }

        return (results, accumulatedDelta, abortReason);
    }

    /// <summary>
    /// Runs the <c>PermissionRequest</c> hooks for a pending approval. Fail-closed: any unexpected
    /// failure denies rather than granting access.
    /// </summary>
    /// <param name="toolName">The tool requesting approval.</param>
    /// <param name="inputJson">The arguments the tool would run with (post-<c>PreToolUse</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<PermissionRequestResult> DecidePermissionByHookAsync(
        string toolName,
        string inputJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var mode = this.options.PermissionModeState?.Mode ?? this.options.PermissionMode;
            var matchedRule = this.permissionRules?.FindMatchedRule(toolName, inputJson);

            return await this.userHooks!
                .RunPermissionRequestAsync(
                    toolName,
                    inputJson,
                    PermissionModeNames.ToWireString(mode),
                    matchedRule,
                    cancellationToken,
                    this.currentDepth,
                    this.currentTaskId)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.LogPermissionRequestHooksFailed(toolName, ex);
            return new PermissionRequestResult
            {
                Decision = PermissionDecisions.Deny,
                Reason = $"permission hook failed: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Applies a <c>PermissionRequest</c> hook's <c>updatedPermissions</c>. Live session state is
    /// always updated; <c>project</c> and <c>user</c> scopes additionally persist to the matching
    /// settings file. A failed write is logged and never fails the turn. Emits
    /// <see cref="IAgentSink.OnPermissionsUpdated"/> for every non-no-op mutation. Refuses
    /// hook-driven escalation to <c>bypassPermissions</c> — a hook cannot grant itself bypass.
    /// </summary>
    /// <param name="update">The parsed update, or <see langword="null"/> when the hook sent none.</param>
    /// <param name="hookCommand">The hook command string, for logging and sink attribution.</param>
    /// <param name="sink">The agent sink to receive <see cref="IAgentSink.OnPermissionsUpdated"/>.</param>
    /// <param name="hookScope">
    /// The scope of the hook(s) that produced the decision. A <see cref="HookScope.Project"/>
    /// hook must not be able to persist rules to the user settings file; the request is clamped
    /// to project scope and a warning is logged.
    /// </param>
    private void ApplyUpdatedPermissions(PermissionUpdate? update, string hookCommand, IAgentSink sink, HookScope hookScope = HookScope.User)
    {
        if (update is null || update.IsEmpty)
        {
            return;
        }

        var appliedAllow = new List<string>();
        var appliedDeny = new List<string>();
        string? appliedMode = null;

        try
        {
            if (update.AddAllow.Count > 0)
            {
                this.permissionRules?.AddAllow(update.AddAllow.Select(PermissionRule.Parse));
                appliedAllow.AddRange(update.AddAllow);
            }

            if (update.AddDeny.Count > 0)
            {
                this.permissionRules?.AddDeny(update.AddDeny.Select(PermissionRule.Parse));
                appliedDeny.AddRange(update.AddDeny);
            }

            if (update.SetMode is { Length: > 0 } requestedMode)
            {
                if (!PermissionModeNames.TryParse(requestedMode, out var parsedMode))
                {
                    this.LogUnknownPermissionMode(requestedMode);
                }
                // I1: refuse hook-driven bypass escalation — a subprocess hook must not be able
                // to disable all future approval prompts without the user's consent.
                else if (parsedMode == PermissionMode.BypassPermissions)
                {
                    this.LogHookBypassEscalationRefused(hookCommand);
                }
                else if (this.options.PermissionModeState is { } state)
                {
                    state.Mode = parsedMode;
                    appliedMode = requestedMode;
                }
            }
        }
        catch (Exception ex)
        {
            this.LogPermissionUpdateFailed(update.Scope, ex);
            return;
        }

        // Emit the sink event for auditability (§8 spec).
        if (appliedAllow.Count > 0 || appliedDeny.Count > 0 || appliedMode is not null)
        {
            sink.OnPermissionsUpdated(hookCommand, appliedMode, appliedAllow, appliedDeny);
        }

        // HIGH-1a: prevent project-scoped hooks from writing to the user settings file.
        // A project hook (from repo code the user cloned) may only persist to project scope.
        // Clamp the requested scope down to project and log the refusal.
        var effectiveScope = update.Scope;
        if (hookScope == HookScope.Project
            && string.Equals(effectiveScope, PermissionUpdate.UserScope, StringComparison.OrdinalIgnoreCase))
        {
            this.LogHookScopeEscalationRefused(hookCommand);
            effectiveScope = PermissionUpdate.ProjectScope;
        }

        var settingsFile = this.ResolveSettingsFileForScope(effectiveScope);
        if (settingsFile is null)
        {
            return; // session scope (or an unknown scope) — live state only.
        }

        // HIGH-1b: filter out over-broad allow rules before persisting to disk. A bare tool name
        // (no argument pattern) matches every call regardless of arguments, silently blanket-
        // disabling prompts for that tool across all future sessions. Bare names may still apply
        // to the current session's in-memory state above; only disk persistence is blocked here.
        // Deny rules are restrictive and do not need this check.
        var allowForDisk = new List<string>(appliedAllow.Count);
        foreach (var rule in appliedAllow)
        {
            var parsed = PermissionRule.Parse(rule);
            if (parsed.ArgPattern is null || parsed.ArgPattern.Length == 0)
            {
                this.LogHookOverbreadAllowRuleRefused(hookCommand, rule);
            }
            else
            {
                allowForDisk.Add(rule);
            }
        }

        SettingsWriter.AddPermissionRules(
            allowForDisk,
            update.AddDeny,
            settingsFile,
            this.logger);
    }

    /// <summary>
    /// Maps an <c>updatedPermissions.scope</c> to the settings file it persists to, or
    /// <see langword="null"/> for the session scope (which never touches disk).
    /// </summary>
    /// <param name="scope">The requested scope: <c>session</c>, <c>project</c> or <c>user</c>.</param>
    private string? ResolveSettingsFileForScope(string scope)
    {
        if (string.Equals(scope, PermissionUpdate.ProjectScope, StringComparison.OrdinalIgnoreCase))
        {
            var workingDirectory = string.IsNullOrWhiteSpace(this.options.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : this.options.WorkingDirectory;
            return Path.Combine(workingDirectory, ".coda", "settings.json");
        }

        if (string.Equals(scope, PermissionUpdate.UserScope, StringComparison.OrdinalIgnoreCase))
        {
            var homeDir = Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(homeDir, ".coda", "settings.json");
        }

        return null;
    }

    private static ToolResultBlock CreateToolResultBlock(
        ToolCallIdentity identity,
        ToolResult result,
        ToolCallStatus status) =>
        new(identity.CallId, result.Content, result.IsError)
        {
            RootTurnId = identity.RootTurnId,
            ActivityId = identity.ActivityId,
            SourceId = identity.SourceId,
            ToolStatus = status.ToString(),
        };

    /// <summary>
    /// Best-effort incremental transcript persist ("record on the go"). Invoked after each
    /// assistant turn and tool cycle so a session killed mid-run still leaves a record of
    /// everything up to the kill; a persistence failure must never break the turn.
    /// </summary>
    private async Task MaybePersistTurnAsync(CancellationToken cancellationToken)
    {
        if (this.persistTurn is null)
        {
            return;
        }

        try
        {
            await this.persistTurn(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.LogPersistTurnFailed(ex);
        }
    }

    /// <summary>A redacted, length-bounded preview of a tool call's JSON arguments for telemetry.</summary>
    internal static string SummarizeToolInput(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return "{}";
        }

        var redacted = SecretRedactor.RedactJson(inputJson);
        return redacted.Length > 500 ? redacted[..500] + "…" : redacted;
    }

    /// <summary>
    /// Emits <see cref="IAgentSink.OnToolProgress"/> every <paramref name="interval"/> while a
    /// tool runs, giving the orchestrator a liveness signal during the tool-execution phase
    /// (the counterpart to the LLM stream-progress pulse). Returns when <paramref name="ct"/>
    /// is cancelled — which the caller does the instant the tool completes.
    /// </summary>
    internal static async Task PumpToolProgressAsync(
        IAgentSink sink,
        string toolName,
        TimeSpan interval,
        long startTimestamp,
        CancellationToken ct)
    {
        await PumpToolProgressAsync(
            interval,
            startTimestamp,
            ct,
            elapsedMs => sink.OnToolProgress(toolName, elapsedMs)).ConfigureAwait(false);
    }

    internal static async Task PumpToolProgressAsync(
        IAgentSink sink,
        ToolCallIdentity identity,
        string toolName,
        TimeSpan interval,
        long startTimestamp,
        CancellationToken ct)
    {
        await PumpToolProgressAsync(
            interval,
            startTimestamp,
            ct,
            elapsedMs => sink.OnToolProgress(identity, toolName, elapsedMs)).ConfigureAwait(false);
    }

    private static async Task PumpToolProgressAsync(
        TimeSpan interval,
        long startTimestamp,
        CancellationToken ct,
        Action<long> emit)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var elapsedMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                emit(elapsedMs);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the tool finished and the heartbeat was cancelled.
        }
    }

    // -----------------------------------------------------------------------
    // LSP helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Polls the registry for up to ~300ms, yielding control between checks, so that
    /// async LSP notifications in-flight have a chance to arrive before the seam runs.
    /// Returns as soon as there is at least one pending diagnostic or the budget expires.
    /// This keeps the turn latency impact negligible and avoids blocking the loop.
    /// </summary>
    private static async Task WaitForDiagnosticsAsync(LspDiagnosticRegistry registry, CancellationToken ct)
    {
        const int MaxPollMs = 300;
        const int PollIntervalMs = 50;
        const int MaxAttempts = MaxPollMs / PollIntervalMs;

        for (var attempt = 0; attempt < MaxAttempts && !ct.IsCancellationRequested; attempt++)
        {
            if (registry.PendingCount > 0)
            {
                return;
            }

            await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
        }
    }

    private static bool IsMutatingFileTool(string toolName)
    {
        return toolName == EditTool.ToolName
            || toolName == WriteFileTool.ToolName
            || toolName == NotebookEditTool.ToolName;
    }

    private async Task NotifyLspFileEditedAsync(string? inputJson, CancellationToken ct)
    {
        try
        {
            // Extract the file path from the tool input JSON.
            // EditTool / WriteFileTool use "path"; NotebookEditTool uses "notebook_path".
            string? path = null;
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("path", out var pathProp) && pathProp.ValueKind == JsonValueKind.String)
            {
                path = pathProp.GetString();
            }
            else if (root.TryGetProperty("notebook_path", out var nbProp) && nbProp.ValueKind == JsonValueKind.String)
            {
                path = nbProp.GetString();
            }

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var fullPath = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(this.options.WorkingDirectory, path));

            // Read the current on-disk content (the tool just wrote it).
            string content;
            try
            {
                content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // File might not exist (e.g. delete edit). Skip gracefully.
                return;
            }

            // Send didChange (opens if needed) then didSave so the server publishes diagnostics.
            await this.lsp!.ChangeFileAsync(fullPath, content, ct).ConfigureAwait(false);
            await this.lsp!.SaveFileAsync(fullPath, ct).ConfigureAwait(false);

            // Clear stale delivered diagnostics for this file so fresh ones surface.
            // The registry canonicalises file URIs and paths to the same key, so passing
            // the local path here matches the server's publishDiagnostics URI.
            this.lspDiagnostics?.ClearDeliveredForFile(fullPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // LSP failures must never break a tool result or the turn.
            this.LogLspNotifyFailed(ex);
        }
    }

    private static string FormatDiagnostics(IReadOnlyList<DiagnosticFile> files, string workingDirectory)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<diagnostics>");

        foreach (var file in files)
        {
            // Convert URI or path to a relative display path.
            var displayPath = file.Uri;
            try
            {
                var localPath = Uri.TryCreate(file.Uri, UriKind.Absolute, out var u) && u.IsFile
                    ? u.LocalPath
                    : file.Uri;
                displayPath = Path.GetRelativePath(workingDirectory, localPath);
            }
            catch
            {
                // Fall back to the raw URI/path.
            }

            foreach (var diag in file.Diagnostics)
            {
                // Wire positions are 0-based; display as 1-based.
                var line = diag.Range.Start.Line + 1;
                var character = diag.Range.Start.Character + 1;
                var severity = diag.Severity.ToString();
                var sourceCode = (diag.Source, diag.Code) switch
                {
                    (not null, not null) => $" ({diag.Source}/{diag.Code})",
                    (not null, null) => $" ({diag.Source})",
                    (null, not null) => $" ({diag.Code})",
                    _ => string.Empty,
                };

                sb.AppendLine($"{displayPath}:{line}:{character} [{severity}] {diag.Message}{sourceCode}");
            }
        }

        sb.Append("</diagnostics>");
        return sb.ToString();
    }
}
