using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Coda.Common;
using Coda.Agent.Tasks;
using Coda.Agent.Compaction;
using Coda.Agent.Goals;
using Coda.Agent.Hooks;
using Coda.Agent.Lsp;
using Coda.Agent.Scheduling;
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
    private readonly IUserQuestionPrompt? userQuestion;
    private readonly UserHookRunner? userHooks;
    private readonly IPlanApprover? planApprover;
    private readonly TaskManager? tasks;
    private readonly string? currentTaskId;
    private readonly int currentDepth;
    private readonly LspServerManager? lsp;
    private readonly LspDiagnosticRegistry? lspDiagnostics;
    private readonly ToolSearchCoordinator? toolSearch;
    private readonly GoalSupervisor? goal;
    private readonly Func<List<ChatMessage>, CancellationToken, Task>? compactAsync;
    private readonly SteeringInbox? steering;
    private readonly AgentExecutionGate? gate;
    private readonly ILogger logger;
    private readonly TimeSpan toolProgressInterval;
    private readonly TimeSpan toolMaxDuration;
    private readonly TimeSpan? transportRetryDelay;
    private readonly Func<CancellationToken, Task>? persistTurn;

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

    public AgentLoop(
        ILlmClient client,
        ToolRegistry tools,
        IPermissionPrompt permissions,
        AgentOptions options,
        ISubagentHost? subagents = null,
        AgentHooks? hooks = null,
        TodoStore? todos = null,
        ScheduledTaskStore? schedules = null,
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
        Func<List<ChatMessage>, CancellationToken, Task>? compactAsync = null,
        SteeringInbox? steering = null,
        ILogger? logger = null,
        TimeSpan? toolProgressInterval = null,
        Func<CancellationToken, Task>? persistTurnAsync = null,
        TimeSpan? toolMaxDuration = null,
        TimeSpan? transportRetryDelay = null,
        AgentExecutionGate? gate = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.tools = tools ?? throw new ArgumentNullException(nameof(tools));
        this.permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.subagents = subagents;
        this.hooks = hooks;
        this.todos = todos;
        this.schedules = schedules;
        this.userQuestion = userQuestion;
        this.userHooks = userHooks;
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
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "turn start: iteration={iteration}, model={model}, historyMessages={messageCount}, tools={toolCount}")]
    private partial void LogTurnStart(int iteration, string model, int messageCount, int toolCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "turn end: iteration={iteration}, stop={stopReason}, toolCalls={toolCount}, textChars={textLength}")]
    private partial void LogTurnEnd(int iteration, string stopReason, int toolCount, int textLength);

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "draining post-sampling hook tasks faulted (best-effort); turn already complete")]
    private partial void LogPostSamplingDrainFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "PostToolUse user hooks failed (best-effort); continuing: tool={toolName}")]
    private partial void LogPostToolUseHooksFailed(string toolName, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LSP edit-seam notify failed (best-effort); tool result and turn unaffected")]
    private partial void LogLspNotifyFailed(Exception ex);

    public async Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(sink);

        var pendingHookTasks = new List<Task>();
        var stopContinuations = 0;
        var stopHookActive = false;
        string? lastInjectedReminder = null;

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
                    && this.options.AutoCompactTokenThreshold > 0
                    && TokenEstimator.Estimate(history) > this.options.AutoCompactTokenThreshold)
                {
                    try
                    {
                        await this.compactAsync(history, cancellationToken).ConfigureAwait(false);
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
                    var steers = this.steering.DrainAll();
                    if (steers.Count > 0)
                    {
                        var steerText = string.Join("\n\n", steers);
                        history.Add(new ChatMessage(ChatRole.User, [new TextBlock(steerText)]));
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
                // When inactive (or no coordinator), use the registry definitions unchanged.
                var toolDefinitions = this.toolSearch is not null && this.toolSearch.IsActive
                    ? this.toolSearch.BuildWireDefinitions(this.tools)
                    : this.tools.Definitions;

                var request = new ChatRequest
                {
                    Model = this.options.Model,
                    MaxTokens = this.options.MaxTokens,
                    System = this.options.SystemPrompt,
                    Messages = history,
                    Tools = toolDefinitions,
                    Effort = this.options.Effort,
                };

                var text = new StringBuilder();
                var toolUses = new List<ToolUseBlock>();
                string? stopReason = null;

                this.LogTurnStart(iteration, this.options.Model, history.Count, toolDefinitions.Count);

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
                        overflowRetried = true;
                        this.LogContextOverflowCompaction(iteration, ex);

                        // Discard the partial turn and summarize the history in place, then retry.
                        text.Clear();
                        toolUses.Clear();
                        stopReason = null;
                        await this.compactAsync(history, cancellationToken).ConfigureAwait(false);
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

                sink.OnAssistantTextComplete();

                this.LogTurnEnd(iteration, stopReason ?? "(none)", toolUses.Count, text.Length);

                var assistantContent = new List<ContentBlock>();
                if (text.Length > 0)
                {
                    assistantContent.Add(new TextBlock(text.ToString()));
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

                    // Fire user Stop hooks (observation only — ignore exit code and errors).
                    if (this.userHooks is not null)
                    {
                        try
                        {
                            await this.userHooks.RunStopAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // User hook errors must not interrupt normal turn completion.
                            this.LogStopHooksFailed(ex);
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
                var resultBlocks = await this.RunToolsAsync(toolUses, sink, cancellationToken).ConfigureAwait(false);
                history.Add(new ChatMessage(ChatRole.User, resultBlocks));

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

    private async Task<List<ContentBlock>> RunToolsAsync(
        IReadOnlyList<ToolUseBlock> toolUses,
        IAgentSink sink,
        CancellationToken cancellationToken)
    {
        var results = new List<ContentBlock>();
        var context = new ToolContext(this.options.WorkingDirectory)
        {
            Sink = sink,
            Subagents = this.subagents,
            Todos = this.todos,
            Schedules = this.schedules,
            UserQuestion = this.userQuestion,
            PlanApprover = this.planApprover,
            Tasks = this.tasks,
            CurrentTaskId = this.currentTaskId,
            CurrentDepth = this.currentDepth,
            Lsp = this.lsp,
            AllTools = this.tools.All,
            OnToolsDiscovered = names => this.toolSearch?.AddDiscovered(names),
            Logger = this.logger,
        };

        foreach (var toolUse in toolUses)
        {
            sink.OnToolCall(toolUse.Name, toolUse.InputJson);
            this.LogToolCall(toolUse.Name, SummarizeToolInput(toolUse.InputJson));

            var tool = this.tools.Resolve(toolUse.Name);
            if (tool is null)
            {
                var unknown = new ToolResult($"Unknown tool '{toolUse.Name}'.", IsError: true);
                sink.OnToolResult(toolUse.Name, unknown);
                results.Add(new ToolResultBlock(toolUse.Id, unknown.Content, unknown.IsError));
                continue;
            }

            // Check user PreToolUse hooks BEFORE the permission prompt so a hook can
            // block a call even when permissions would otherwise allow it.
            if (this.userHooks is not null && this.userHooks.HasPreToolUse)
            {
                var hookResult = await this.userHooks
                    .RunPreToolUseAsync(toolUse.Name, toolUse.InputJson, cancellationToken)
                    .ConfigureAwait(false);

                if (hookResult.Block)
                {
                    var blocked = new ToolResult(
                        $"Blocked by hook: {hookResult.Message}",
                        IsError: true);
                    sink.OnToolResult(toolUse.Name, blocked);
                    results.Add(new ToolResultBlock(toolUse.Id, blocked.Content, blocked.IsError));
                    continue;
                }
            }

            if (!tool.IsReadOnly)
            {
                var allowed = await this.permissions.RequestAsync(tool, toolUse.InputJson, cancellationToken).ConfigureAwait(false);
                if (!allowed)
                {
                    var denied = new ToolResult("Permission denied by the user.", IsError: true);
                    sink.OnToolResult(toolUse.Name, denied);
                    results.Add(new ToolResultBlock(toolUse.Id, denied.Content, denied.IsError));
                    continue;
                }
            }

            ToolResult result;
            // Pulse a liveness heartbeat while the tool runs so the orchestrator's idle
            // watchdog can tell "a long tool is working" from "the process is wedged". The
            // pump is torn down in the finally — including on the OperationCanceledException
            // rethrow path — so it can never outlive the tool call.
            var toolStartedAt = Stopwatch.GetTimestamp();
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeat = PumpToolProgressAsync(sink, toolUse.Name, this.toolProgressInterval, toolStartedAt, heartbeatCts.Token);

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
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(toolUse.InputJson) ? "{}" : toolUse.InputJson);

                // Recompute the sandbox flag per individual tool execution (not once per batch) so a
                // mid-batch mode change (Default→Bypass or back) applies to the very next tool. Read
                // the mode live from the shared state; fall back to the snapshot mode for a fixed
                // headless run with no shared state.
                var toolContext = context with
                {
                    AllowOutsideWorkingDirectory =
                        (this.options.PermissionModeState?.Mode ?? this.options.PermissionMode) == PermissionMode.BypassPermissions,
                };
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

            // Fire PostToolUse hooks (observation only — ignore exit code and errors).
            if (this.userHooks is not null)
            {
                try
                {
                    await this.userHooks
                        .RunPostToolUseAsync(toolUse.Name, toolUse.InputJson, result.Content, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // User hook errors must not interrupt normal turn completion.
                    this.LogPostToolUseHooksFailed(toolUse.Name, ex);
                }
            }

            sink.OnToolResult(toolUse.Name, result);
            this.LogToolResult(toolUse.Name, result.IsError, result.Content.Length);
            results.Add(new ToolResultBlock(toolUse.Id, result.Content, result.IsError));

            // EDIT SEAM: when a mutating file tool succeeds, notify the LSP server
            // about the new file content (change + save) so it can publish diagnostics.
            // Failures are swallowed — LSP must never break a tool result.
            if (!result.IsError && this.lsp is not null && IsMutatingFileTool(toolUse.Name))
            {
                await this.NotifyLspFileEditedAsync(toolUse.InputJson, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

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
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var elapsedMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                sink.OnToolProgress(toolName, elapsedMs);
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
