using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Agent.Hooks;

/// <summary>
/// Owns ordered hook execution and output merging for all user-configured lifecycle events.
/// </summary>
/// <remarks>
/// <para>
/// Hooks for a given event run sequentially in configuration order. Their outputs are merged
/// by the rules in §6 of the agent-hooks proposal: strictest decision wins, reasons are joined,
/// <c>Continue:false</c> short-circuits remaining hooks, and <c>systemMessage</c> /
/// <c>additionalContext</c> are concatenated.
/// </para>
/// <para>
/// Execution is delegated to the injected <see cref="IHookExecutor"/>; the bus is responsible
/// only for timeout management, exit-code interpretation, output parsing, cap/spill, and merging.
/// Inject a fake executor in unit tests to exercise merge logic without spawning processes.
/// </para>
/// </remarks>
public sealed partial class HookBus
{
    /// <summary>Maximum characters kept in memory per stdout/stderr stream.</summary>
    internal const int OutputCap = 10_000;

    private readonly IReadOnlyList<UserHook> hooks;
    private readonly IHookExecutor executor;
    private readonly HookContext? context;
    private readonly Func<DateTimeOffset>? clock;
    private readonly Func<string>? spillDirFactory;
    private readonly ILogger logger;
    private int spillCounter;

    /// <summary>
    /// Initialises the bus.
    /// </summary>
    /// <param name="hooks">All user-configured hooks for this session.</param>
    /// <param name="executor">
    /// Process executor. Defaults to <see cref="ShellHookExecutor"/> when <see langword="null"/>.
    /// </param>
    /// <param name="context">
    /// Session-level envelope values written into every hook payload.
    /// When <see langword="null"/> the envelope fields are omitted (backward-compatible with
    /// tests that do not supply a context).
    /// </param>
    /// <param name="clock">
    /// Clock used for the payload timestamp and spill-file names.
    /// Defaults to <see cref="DateTimeOffset.UtcNow"/>.
    /// </param>
    /// <param name="spillDirFactory">
    /// Factory returning the directory for output-overflow spill files.
    /// Defaults to <c>~/.coda/hook-output</c>. Inject a temp path in tests.
    /// </param>
    /// <param name="logger">Logger for warnings and override notifications.</param>
    public HookBus(
        IReadOnlyList<UserHook> hooks,
        IHookExecutor? executor = null,
        HookContext? context = null,
        Func<DateTimeOffset>? clock = null,
        Func<string>? spillDirFactory = null,
        ILogger? logger = null)
    {
        this.hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        this.executor = executor ?? new ShellHookExecutor();
        this.context = context;
        this.clock = clock;
        this.spillDirFactory = spillDirFactory;
        this.logger = logger ?? NullLogger.Instance;
        this.HasPreToolUse = hooks.Any(h => string.Equals(h.Event, "PreToolUse", StringComparison.OrdinalIgnoreCase));
        this.HasPostToolUse = hooks.Any(h => string.Equals(h.Event, "PostToolUse", StringComparison.OrdinalIgnoreCase));
        this.HasPermissionRequest = hooks.Any(h => string.Equals(h.Event, "PermissionRequest", StringComparison.OrdinalIgnoreCase));
        this.HasUserPromptSubmit = hooks.Any(h => string.Equals(h.Event, "UserPromptSubmit", StringComparison.OrdinalIgnoreCase));
        this.HasSessionStart = hooks.Any(h => string.Equals(h.Event, "SessionStart", StringComparison.OrdinalIgnoreCase));
        this.HasSessionEnd = hooks.Any(h => string.Equals(h.Event, "SessionEnd", StringComparison.OrdinalIgnoreCase));
        this.HasNotification = hooks.Any(h => string.Equals(h.Event, "Notification", StringComparison.OrdinalIgnoreCase));
        this.HasStop = hooks.Any(h => string.Equals(h.Event, "Stop", StringComparison.OrdinalIgnoreCase));
        this.HasAgentResponse = hooks.Any(h => string.Equals(h.Event, "AgentResponse", StringComparison.OrdinalIgnoreCase));
        this.AnyHookMutatesDisplay = hooks.Any(h =>
            string.Equals(h.Event, "AgentResponse", StringComparison.OrdinalIgnoreCase)
            && h.Mutates is not null
            && h.Mutates.Any(m =>
                string.Equals(m, "displayContent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(m, "modifiedResponse", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>True when at least one <c>PreToolUse</c> hook is configured.</summary>
    public bool HasPreToolUse { get; }

    /// <summary>True when at least one <c>PostToolUse</c> hook is configured.</summary>
    public bool HasPostToolUse { get; }

    /// <summary>True when at least one <c>PermissionRequest</c> hook is configured.</summary>
    public bool HasPermissionRequest { get; }

    /// <summary>True when at least one <c>UserPromptSubmit</c> hook is configured.</summary>
    public bool HasUserPromptSubmit { get; }

    /// <summary>True when at least one <c>SessionStart</c> hook is configured.</summary>
    public bool HasSessionStart { get; }

    /// <summary>True when at least one <c>SessionEnd</c> hook is configured.</summary>
    public bool HasSessionEnd { get; }

    /// <summary>True when at least one <c>Notification</c> hook is configured.</summary>
    public bool HasNotification { get; }

    /// <summary>True when at least one <c>Stop</c> hook is configured.</summary>
    public bool HasStop { get; }

    /// <summary>True when at least one <c>AgentResponse</c> hook is configured.</summary>
    public bool HasAgentResponse { get; }

    /// <summary>
    /// True when any configured <c>AgentResponse</c> hook declares <c>"displayContent"</c> or
    /// <c>"modifiedResponse"</c> in its <c>mutates</c> list. Consulted once at session start by
    /// the TUI to decide whether to buffer assistant text before display.
    /// </summary>
    public bool AnyHookMutatesDisplay { get; }

    // -------------------------------------------------------------------------
    // Public run-methods (mirror the old UserHookRunner public surface)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs all matching <c>PreToolUse</c> hooks in configuration order and returns the
    /// merged result. A hook exiting with code 1 (or any other non-zero code) blocks the
    /// tool call because <c>PreToolUse</c> defaults to fail-closed.
    /// </summary>
    /// <remarks>
    /// A hook may also return <c>hookSpecificOutput.modifiedInput</c>: a JSON object that
    /// <strong>fully replaces</strong> the tool arguments (it is never merged into them).
    /// A non-object value is ignored with a warning; across multiple hooks the last writer wins
    /// and the override is logged.
    /// </remarks>
    /// <param name="toolName">The name of the tool about to be called.</param>
    /// <param name="inputJson">The tool's input as a JSON string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth for this invocation: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The task identifier for this invocation, or <see langword="null"/> for the main agent.</param>
    public async Task<UserHookResult> RunPreToolUseAsync(
        string toolName,
        string inputJson,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null)
    {
        var matching = this.GetMatchingHooks("PreToolUse", toolName);
        if (matching.Count == 0)
        {
            return UserHookResult.Allow;
        }

        var payload = this.BuildPrePayload(toolName, inputJson, depth, taskId);
        var outputs = await this.RunHooksAsync(matching, "PreToolUse", payload, ct).ConfigureAwait(false);

        var merged = ToUserHookResult(MergeOutputs(outputs));
        if (merged.Block)
        {
            return merged;
        }

        var (modifiedInput, byHookCommand) = this.ExtractLastJsonObject(matching, outputs, "modifiedInput");
        return modifiedInput is null
            ? merged
            : merged with { ModifiedInput = modifiedInput, ByHookCommand = byHookCommand };
    }

    /// <summary>
    /// Runs all matching <c>PostToolUse</c> hooks and returns the merged result. Exit codes and
    /// errors never fail the tool call (fail-open default) — the tool has already run and its
    /// side effects cannot be undone.
    /// </summary>
    /// <remarks>
    /// A hook may return <c>hookSpecificOutput.modifiedResult</c> to replace the result text the
    /// model sees, or <c>decision:"block"</c> with a <c>reason</c> that replaces the result
    /// entirely. Neither un-runs the tool. Both are last-writer-wins across hooks.
    /// </remarks>
    /// <param name="toolName">The name of the tool that was called.</param>
    /// <param name="inputJson">The tool's input as a JSON string.</param>
    /// <param name="toolResultText">The tool result text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth for this invocation: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The task identifier for this invocation, or <see langword="null"/> for the main agent.</param>
    /// <param name="errorText">
    /// The failure text when the tool call failed (threw, or exceeded its time ceiling), written to
    /// the payload's <c>error</c> field; <see langword="null"/> for a successful call.
    /// </param>
    public async Task<PostToolUseResult> RunPostToolUseAsync(
        string toolName,
        string inputJson,
        string toolResultText,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null,
        string? errorText = null)
    {
        var matching = this.GetMatchingHooks("PostToolUse", toolName);
        if (matching.Count == 0)
        {
            return PostToolUseResult.NoChange;
        }

        var payload = this.BuildPostPayload(toolName, inputJson, toolResultText, errorText, depth, taskId);
        List<HookOutput> outputs;
        try
        {
            outputs = await this.RunHooksAsync(matching, "PostToolUse", payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Individual hook errors are already swallowed in RunSingleHookAsync;
            // this outer catch guards against unexpected failures in the loop itself.
            return PostToolUseResult.NoChange;
        }

        return this.MergePostToolUseOutputs(matching, outputs);
    }

    /// <summary>
    /// Runs all matching <c>PermissionRequest</c> hooks. The event fires after <c>PreToolUse</c>
    /// passed and only for a tool that would otherwise render an interactive approval prompt.
    /// Policy: 10 s timeout, fail-closed — a broken hook denies rather than grants.
    /// </summary>
    /// <param name="toolName">The name of the tool requesting approval.</param>
    /// <param name="inputJson">The tool's input as a JSON string.</param>
    /// <param name="permissionMode">The live permission mode (e.g. <c>"default"</c>).</param>
    /// <param name="matchedRule">
    /// The matching permission rule in <c>allow:rule</c> / <c>deny:rule</c> form, or
    /// <see langword="null"/> when no configured rule matches this call.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth for this invocation: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The task identifier for this invocation, or <see langword="null"/> for the main agent.</param>
    public async Task<PermissionRequestResult> RunPermissionRequestAsync(
        string toolName,
        string inputJson,
        string permissionMode,
        string? matchedRule,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null)
    {
        var matching = this.GetMatchingHooks("PermissionRequest", toolName);
        if (matching.Count == 0)
        {
            return PermissionRequestResult.Prompt;
        }

        var payload = this.BuildPermissionRequestPayload(toolName, inputJson, permissionMode, matchedRule, depth, taskId);
        List<HookOutput> outputs;
        try
        {
            outputs = await this.RunHooksAsync(matching, "PermissionRequest", payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail-closed: an unexpected loop-level failure denies.
            return new PermissionRequestResult
            {
                Decision = PermissionDecisions.Deny,
                Reason = $"permission hook failed: {ex.Message}",
                ByHookCommand = matching[0].Command,
            };
        }

        return this.MergePermissionRequestOutputs(matching, outputs);
    }

    /// <summary>
    /// Runs all matching <c>Stop</c> hooks. Exit codes and errors are ignored (fail-open default).
    /// The merged output is not acted on in Phase 0.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth for this invocation: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The task identifier for this invocation, or <see langword="null"/> for the main agent.</param>
    public async Task RunStopAsync(CancellationToken ct, int depth = 0, string? taskId = null)
    {
        var matching = this.GetMatchingHooks("Stop", toolName: null);
        if (matching.Count == 0)
        {
            return;
        }

        var payload = this.BuildStopPayload(depth, taskId);
        try
        {
            await this.RunHooksAsync(matching, "Stop", payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Same as PostToolUse.
        }
    }

    /// <summary>
    /// Runs all <c>Stop</c> hooks with full blocking power. A hook returning
    /// <c>decision:"block"</c> (or exit code 2) forces continuation and injects its
    /// <paramref name="reason"/> as the next instruction in history, exactly as
    /// <see cref="AgentHooks.RunStopHooksAsync"/> does for in-process hooks.
    /// Both paths share one <c>stopContinuations</c> counter so they cannot each
    /// spend the full <c>MaxStopContinuations</c> budget independently.
    /// Policy: 10 s timeout, fail-open — a broken stop hook must not trap the agent.
    /// </summary>
    /// <param name="stopReason">The stop reason emitted by the model, if any.</param>
    /// <param name="iterations">Number of agent iterations completed so far.</param>
    /// <param name="continuationCount">Continuations already consumed (shared counter).</param>
    /// <param name="stopHookActive">
    /// <see langword="true"/> when this call is itself a continuation driven by a prior hook block;
    /// <see langword="false"/> on the first natural stop.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth.</param>
    /// <param name="taskId">Task identifier, or <see langword="null"/> for the main agent.</param>
    public async Task<StopHookOutcome> RunStopWithOutcomeAsync(
        string? stopReason,
        int iterations,
        int continuationCount,
        bool stopHookActive,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null)
    {
        var matching = this.GetMatchingHooks("Stop", toolName: null);
        if (matching.Count == 0)
        {
            return StopHookOutcome.Stop;
        }

        var payload = this.BuildStopWithOutcomePayload(stopReason, iterations, continuationCount, stopHookActive, depth, taskId);
        List<HookOutput> outputs;
        try
        {
            outputs = await this.RunHooksAsync(matching, "Stop", payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fail-open: a broken stop hook must not trap the agent in a loop.
            return StopHookOutcome.Stop;
        }

        var merged = MergeOutputs(outputs);

        // A block decision forces continuation — inject the reason as the next instruction.
        if (string.Equals(merged.Decision, "block", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(merged.Reason))
        {
            return new StopHookOutcome(ShouldContinue: true, InjectedMessage: merged.Reason);
        }

        return StopHookOutcome.Stop;
    }

    /// <summary>
    /// Runs all <c>AgentResponse</c> hooks after the assistant's final text is settled.
    /// Policy: 10 s timeout, fail-open — a broken hook leaves the response unchanged.
    /// </summary>
    /// <param name="response">The final assistant text.</param>
    /// <param name="stopReason">The stop reason emitted by the model, if any.</param>
    /// <param name="usage">Token usage breakdown for this response.</param>
    /// <param name="durationMs">Wall-clock duration of the agent turn in milliseconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth.</param>
    /// <param name="taskId">Task identifier, or <see langword="null"/> for the main agent.</param>
    public async Task<AgentResponseResult> RunAgentResponseAsync(
        string response,
        string? stopReason,
        TokenUsage usage,
        long durationMs,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null)
    {
        var matching = this.GetMatchingHooks("AgentResponse", toolName: null);
        if (matching.Count == 0)
        {
            return AgentResponseResult.NoChange;
        }

        var payload = this.BuildAgentResponsePayload(response, stopReason, usage, durationMs, depth, taskId);
        List<HookOutput> outputs;
        try
        {
            outputs = await this.RunHooksAsync(matching, "AgentResponse", payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fail-open: a broken hook leaves the response unchanged.
            return AgentResponseResult.NoChange;
        }

        return this.MergeAgentResponseOutputs(matching, outputs);
    }

    /// <summary>
    /// Runs all matching <c>UserPromptSubmit</c> hooks in configuration order and returns the
    /// merged result. A broken or timed-out hook <strong>blocks</strong> (fail-closed, FailOpen
    /// defaults to <see langword="false"/> for this event) because a policy gate that silently
    /// permits on error is no gate at all.
    /// </summary>
    /// <param name="prompt">Concatenated text of all text blocks in the user message.</param>
    /// <param name="attachments">Non-text content-block kinds present (e.g. <c>"image"</c>), or empty.</param>
    /// <param name="historyLength">Number of messages in history before this turn is appended.</param>
    /// <param name="model">The model identifier for this turn.</param>
    /// <param name="permissionMode">The permission mode string for this turn (e.g. <c>"default"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The running task id, or <see langword="null"/> for the main agent.</param>
    public async Task<UserPromptSubmitResult> RunUserPromptSubmitAsync(
        string prompt,
        IReadOnlyList<string> attachments,
        int historyLength,
        string model,
        string permissionMode,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null)
    {
        var matching = this.GetMatchingHooks("UserPromptSubmit", toolName: null);
        if (matching.Count == 0)
        {
            return UserPromptSubmitResult.Allow;
        }

        var payload = this.BuildUserPromptSubmitPayload(prompt, attachments, historyLength, model, permissionMode, depth, taskId);
        var pairs = new List<(UserHook Hook, HookOutput Output)>(matching.Count);
        foreach (var hook in matching)
        {
            var output = await this.RunSingleHookAsync(hook, "UserPromptSubmit", payload, ct).ConfigureAwait(false);
            pairs.Add((hook, output));
            if (!output.Continue)
            {
                break;
            }
        }

        return this.MergeUserPromptSubmitOutputs(pairs);
    }

    // -------------------------------------------------------------------------
    // Session lifecycle run-methods (Phase 2)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs all <c>SessionStart</c> hooks in configuration order and returns the merged
    /// session-scoped outputs. Fail-open: a broken or timed-out hook returns
    /// <see cref="SessionStartResult.Empty"/> rather than blocking the session.
    /// </summary>
    /// <param name="source">Session source: <c>"new"</c>, <c>"resume"</c>, or <c>"scheduled"</c>.</param>
    /// <param name="model">The model identifier for this session.</param>
    /// <param name="permissionMode">The permission mode string (e.g. <c>"default"</c>).</param>
    /// <param name="transcriptPath">Absolute path to the transcript file, or <see langword="null"/>.</param>
    /// <param name="resumedFrom">Session id being resumed, or <see langword="null"/> for a new session.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SessionStartResult> RunSessionStartAsync(
        string source,
        string model,
        string permissionMode,
        string? transcriptPath,
        string? resumedFrom,
        CancellationToken ct)
    {
        var matching = this.GetMatchingHooks("SessionStart", toolName: null);
        if (matching.Count == 0)
        {
            return SessionStartResult.Empty;
        }

        var payload = this.BuildSessionStartPayload(source, model, permissionMode, transcriptPath, resumedFrom);
        List<HookOutput> outputs;
        try
        {
            outputs = await this.RunHooksAsync(matching, "SessionStart", payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Unexpected loop-level failure: fail-open, return empty result.
            return SessionStartResult.Empty;
        }

        return ParseSessionStartOutputs(outputs);
    }

    /// <summary>
    /// Runs all <c>SessionEnd</c> hooks (observation-only; outputs are discarded).
    /// The caller is responsible for imposing a hard timeout via
    /// <paramref name="ct"/> before awaiting this task.
    /// </summary>
    /// <param name="reason">Why the session ended: <c>"exit"</c>, <c>"interrupt"</c>, <c>"error"</c>, or <c>"shutdown"</c>.</param>
    /// <param name="durationMs">Wall-clock session duration in milliseconds.</param>
    /// <param name="turnCount">Number of turns completed this session.</param>
    /// <param name="usage">Accumulated token usage for the session.</param>
    /// <param name="transcriptPath">Absolute path to the transcript file, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token (should carry the hard 2 s deadline).</param>
    public async Task RunSessionEndAsync(
        string reason,
        long durationMs,
        int turnCount,
        TokenUsage usage,
        string? transcriptPath,
        CancellationToken ct)
    {
        var matching = this.GetMatchingHooks("SessionEnd", toolName: null);
        if (matching.Count == 0)
        {
            return;
        }

        var payload = this.BuildSessionEndPayload(reason, durationMs, turnCount, usage, transcriptPath);
        try
        {
            await this.RunHooksAsync(matching, "SessionEnd", payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Observation-only; swallow unexpected loop-level failures.
        }
    }

    /// <summary>
    /// Runs all <c>Notification</c> hooks (observation-only; outputs are discarded).
    /// Callers should fire-and-forget so notification latency never blocks the agent.
    /// </summary>
    /// <param name="kind">Notification kind: <c>"idle"</c>, <c>"approval"</c>, or <c>"task-complete"</c>.</param>
    /// <param name="message">Human-readable description of the event.</param>
    /// <param name="taskId">The background task id, or <see langword="null"/> for non-task notifications.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunNotificationAsync(
        string kind,
        string message,
        string? taskId,
        CancellationToken ct)
    {
        var matching = this.GetMatchingHooks("Notification", toolName: null);
        if (matching.Count == 0)
        {
            return;
        }

        var payload = this.BuildNotificationPayload(kind, message, taskId);
        try
        {
            await this.RunHooksAsync(matching, "Notification", payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Observation-only; swallow.
        }
    }

    // -------------------------------------------------------------------------
    // Hook loop
    // -------------------------------------------------------------------------

    private async Task<List<HookOutput>> RunHooksAsync(
        IReadOnlyList<UserHook> matching,
        string eventName,
        string payload,
        CancellationToken ct)
    {
        var outputs = new List<HookOutput>(matching.Count);
        foreach (var hook in matching)
        {
            var output = await this.RunSingleHookAsync(hook, eventName, payload, ct).ConfigureAwait(false);
            outputs.Add(output);
            if (!output.Continue)
            {
                break; // Continue:false short-circuits remaining hooks
            }
        }

        return outputs;
    }

    private async Task<HookOutput> RunSingleHookAsync(
        UserHook hook,
        string eventName,
        string payload,
        CancellationToken ct)
    {
        var policy = HookEventPolicy.Get(eventName);
        var timeoutSeconds = hook.TimeoutSeconds ?? policy.TimeoutSeconds;
        var failOpen = hook.FailOpen ?? policy.FailOpen;

        using var hookCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hookCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var (exitCode, rawStdout, rawStderr) = await this.executor
                .ExecAsync(hook.Command, payload, hookCts.Token)
                .ConfigureAwait(false);

            if (exitCode == 0)
            {
                // Apply cap/spill for the display/logging side effect (creates a spill file
                // when the output is large) but parse the full, bounded stdout so that a
                // valid JSON {"decision":"block",...} longer than OutputCap characters is
                // never silently truncated into invalid JSON and downgraded to allow.
                this.ApplyCapWithSpill(rawStdout, eventName, "stdout");
                this.ApplyCapWithSpill(rawStderr, eventName, "stderr");
                return HookOutputParser.Parse(rawStdout);
            }

            // For non-zero exits the capped copy is the human-facing reason.
            var stdout = this.ApplyCapWithSpill(rawStdout, eventName, "stdout");
            var stderr = this.ApplyCapWithSpill(rawStderr, eventName, "stderr");

            if (exitCode == 2)
            {
                var reason = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                    : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
                    : "blocked by hook (exit 2)";
                return new HookOutput { Decision = "block", Reason = reason };
            }

            // Other non-zero exit: warn, apply fail-open policy.
            this.LogHookNonZeroExit(hook.Command, exitCode, eventName);
            if (failOpen)
            {
                return HookOutput.NoOp;
            }

            var blockReason = !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
                : !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                : $"hook exited with code {exitCode}";
            return new HookOutput { Decision = "block", Reason = blockReason };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Hook-specific timeout fired, not the caller's cancellation.
            this.LogHookTimeout(hook.Command, timeoutSeconds, eventName);
            return failOpen
                ? HookOutput.NoOp
                : new HookOutput { Decision = "block", Reason = $"hook timed out after {timeoutSeconds}s" };
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation — propagate.
            throw;
        }
        catch (Exception ex)
        {
            this.LogHookException(hook.Command, eventName, ex);
            return failOpen
                ? HookOutput.NoOp
                : new HookOutput { Decision = "block", Reason = $"hook execution failed: {ex.Message}" };
        }
    }

    // -------------------------------------------------------------------------
    // Merge
    // -------------------------------------------------------------------------

    /// <summary>
    /// Merges multiple hook outputs into a single result following §6 of the proposal.
    /// Exposed as public to enable direct unit testing of the merge logic without spawning processes.
    /// </summary>
    public static HookOutput MergeOutputs(IReadOnlyList<HookOutput> outputs)
    {
        if (outputs.Count == 0)
        {
            return HookOutput.NoOp;
        }

        if (outputs.Count == 1)
        {
            return outputs[0];
        }

        string? decision = null;
        var reasons = new List<string>();
        var systemMessages = new List<string>();
        var additionalContexts = new List<string>();
        var continueRun = true;
        string? stopReason = null;
        var suppressOutput = false;

        foreach (var output in outputs)
        {
            // Decision: strictest wins (allow < ask < deny < block).
            decision = StrictestDecision(decision, output.Decision);

            // Reasons: collected from blocking / denying hooks only.
            if (IsBlockingDecision(output.Decision) && !string.IsNullOrEmpty(output.Reason))
            {
                reasons.Add(output.Reason);
            }

            // systemMessage: concatenated in order.
            if (!string.IsNullOrEmpty(output.SystemMessage))
            {
                systemMessages.Add(output.SystemMessage);
            }

            // Continue: false wins.
            if (!output.Continue)
            {
                continueRun = false;
            }

            // StopReason: last writer wins.
            if (output.StopReason is not null)
            {
                stopReason = output.StopReason;
            }

            // SuppressOutput: any true wins (semantically: once suppressed, keep suppressed).
            if (output.SuppressOutput)
            {
                suppressOutput = true;
            }

            // additionalContext inside hookSpecificOutput: concatenated in order.
            if (output.HookSpecificOutput is { } specific
                && specific.TryGetPropertyValue("additionalContext", out var addCtxNode)
                && addCtxNode is JsonValue addCtxValue
                && addCtxValue.TryGetValue<string>(out var addCtx)
                && !string.IsNullOrWhiteSpace(addCtx))
            {
                additionalContexts.Add(addCtx);
            }
        }

        JsonObject? mergedSpecific = additionalContexts.Count > 0
            ? new JsonObject { ["additionalContext"] = string.Join("\n\n", additionalContexts) }
            : null;

        return new HookOutput
        {
            Decision = decision,
            Reason = reasons.Count > 0 ? string.Join("\n\n", reasons) : null,
            SystemMessage = systemMessages.Count > 0 ? string.Join("\n", systemMessages) : null,
            Continue = continueRun,
            StopReason = stopReason,
            SuppressOutput = suppressOutput,
            HookSpecificOutput = mergedSpecific,
        };
    }

    private static string? StrictestDecision(string? current, string? candidate)
    {
        if (candidate is null)
        {
            return current;
        }

        if (current is null)
        {
            return candidate;
        }

        return DecisionRank(candidate) > DecisionRank(current) ? candidate : current;
    }

    private static int DecisionRank(string? decision) => decision?.ToLowerInvariant() switch
    {
        "allow" => 0,
        "ask"   => 1,
        "deny"  => 2,
        "block" => 3,
        _       => 0,
    };

    private static bool IsBlockingDecision(string? decision) =>
        decision is not null
        && (string.Equals(decision, "block", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "deny", StringComparison.OrdinalIgnoreCase));

    private static UserHookResult ToUserHookResult(HookOutput merged)
    {
        if (!merged.Continue)
        {
            return new UserHookResult(Block: true, merged.StopReason ?? merged.Reason ?? "hook requested stop");
        }

        if (IsBlockingDecision(merged.Decision))
        {
            return new UserHookResult(Block: true, merged.Reason ?? "blocked by hook");
        }

        return UserHookResult.Allow;
    }

    /// <summary>
    /// Merges <c>PostToolUse</c> hook outputs: a <c>block</c> decision wins outright (its reason
    /// replaces the result), otherwise <c>modifiedResult</c> is applied last-writer-wins.
    /// </summary>
    private PostToolUseResult MergePostToolUseOutputs(
        IReadOnlyList<UserHook> matching,
        IReadOnlyList<HookOutput> outputs)
    {
        if (outputs.Count == 0)
        {
            return PostToolUseResult.NoChange;
        }

        var merged = MergeOutputs(outputs);

        string? modifiedResult = null;
        string? byHookCommand = null;
        for (var i = 0; i < outputs.Count; i++)
        {
            var specific = outputs[i].HookSpecificOutput;
            if (specific is null || !TryGetStringAllowEmpty(specific, "modifiedResult", out var mr))
            {
                continue;
            }

            if (modifiedResult is not null)
            {
                this.LogFieldOverride("modifiedResult", modifiedResult, mr!);
            }

            modifiedResult = mr;
            byHookCommand = i < matching.Count ? matching[i].Command : null;
        }

        if (IsBlockingDecision(merged.Decision))
        {
            var reason = merged.Reason ?? "blocked by hook";
            return new PostToolUseResult(Block: true, reason, modifiedResult, byHookCommand ?? matching[0].Command);
        }

        return modifiedResult is null
            ? PostToolUseResult.NoChange
            : new PostToolUseResult(Block: false, null, modifiedResult, byHookCommand);
    }

    /// <summary>
    /// Merges <c>PermissionRequest</c> hook outputs. The decision is resolved strictest-first
    /// (<c>deny</c> beats <c>prompt</c> beats <c>allow</c>) so a permissive hook can never
    /// override a restrictive one; <c>modifiedInput</c> and <c>updatedPermissions</c> are
    /// last-writer-wins.
    /// </summary>
    private PermissionRequestResult MergePermissionRequestOutputs(
        IReadOnlyList<UserHook> matching,
        IReadOnlyList<HookOutput> outputs)
    {
        if (outputs.Count == 0)
        {
            return PermissionRequestResult.Prompt;
        }

        string? decision = null;
        var reasons = new List<string>();
        string? decisionHookCommand = null;
        PermissionUpdate? update = null;

        for (var i = 0; i < outputs.Count; i++)
        {
            var output = outputs[i];
            var hookCommand = i < matching.Count ? matching[i].Command : null;

            // Continue:false is a hard stop — treat it as a denial rather than a silent allow.
            var candidate = output.Continue
                ? NormalizePermissionDecision(output.Decision)
                : PermissionDecisions.Deny;

            if (candidate is not null
                && (decision is null || PermissionDecisionRank(candidate) > PermissionDecisionRank(decision)))
            {
                decision = candidate;
                decisionHookCommand = hookCommand;
            }

            if (!string.IsNullOrEmpty(output.Reason)
                && (string.Equals(candidate, PermissionDecisions.Deny, StringComparison.OrdinalIgnoreCase)
                    || !output.Continue))
            {
                reasons.Add(output.Reason);
            }

            if (output.HookSpecificOutput is { } specific
                && ParsePermissionUpdate(specific) is { } parsed)
            {
                update = parsed;
            }
        }

        var (modifiedInput, modifiedByCommand) = this.ExtractLastJsonObject(matching, outputs, "modifiedInput");

        return new PermissionRequestResult
        {
            Decision = decision ?? PermissionDecisions.Prompt,
            Reason = reasons.Count > 0 ? string.Join("\n\n", reasons) : null,
            ModifiedInput = modifiedInput,
            UpdatedPermissions = update,
            ByHookCommand = decisionHookCommand ?? modifiedByCommand,
        };
    }

    /// <summary>
    /// Maps a raw hook decision onto the <c>PermissionRequest</c> vocabulary. <c>block</c> and
    /// <c>ask</c> are folded onto <c>deny</c> and <c>prompt</c> respectively; an unrecognised or
    /// absent value expresses no opinion (<see langword="null"/>).
    /// </summary>
    private static string? NormalizePermissionDecision(string? decision) => decision?.ToLowerInvariant() switch
    {
        "allow"  => PermissionDecisions.Allow,
        "prompt" => PermissionDecisions.Prompt,
        "ask"    => PermissionDecisions.Prompt,
        "deny"   => PermissionDecisions.Deny,
        "block"  => PermissionDecisions.Deny,
        _        => null,
    };

    private static int PermissionDecisionRank(string decision) => decision switch
    {
        PermissionDecisions.Allow  => 0,
        PermissionDecisions.Prompt => 1,
        PermissionDecisions.Deny   => 2,
        _                          => 0,
    };

    /// <summary>Parses <c>hookSpecificOutput.updatedPermissions</c>, or returns null when absent/empty.</summary>
    private static PermissionUpdate? ParsePermissionUpdate(JsonObject specific)
    {
        if (!specific.TryGetPropertyValue("updatedPermissions", out var node) || node is not JsonObject updated)
        {
            return null;
        }

        List<string> addAllow = [];
        List<string> addDeny = [];
        if (updated.TryGetPropertyValue("addRules", out var rulesNode) && rulesNode is JsonObject rules)
        {
            if (rules.TryGetPropertyValue("allow", out var allowNode) && allowNode is JsonArray allowArray)
            {
                addAllow = [.. ReadStringArray(allowArray)];
            }

            if (rules.TryGetPropertyValue("deny", out var denyNode) && denyNode is JsonArray denyArray)
            {
                addDeny = [.. ReadStringArray(denyArray)];
            }
        }

        TryGetString(updated, "setMode", out var setMode);
        var scope = TryGetString(updated, "scope", out var scopeValue)
            ? scopeValue!.Trim()
            : PermissionUpdate.SessionScope;

        var result = new PermissionUpdate(addAllow, addDeny, setMode, scope);
        return result.IsEmpty ? null : result;
    }

    /// <summary>
    /// Extracts the last <c>hookSpecificOutput.<paramref name="key"/></c> value that is a JSON
    /// object, returning its compact JSON text plus the command of the hook that produced it.
    /// A value of any other kind is ignored with a warning; an override is logged.
    /// </summary>
    private (string? Json, string? ByHookCommand) ExtractLastJsonObject(
        IReadOnlyList<UserHook> matching,
        IReadOnlyList<HookOutput> outputs,
        string key)
    {
        string? json = null;
        string? byHookCommand = null;

        for (var i = 0; i < outputs.Count; i++)
        {
            var specific = outputs[i].HookSpecificOutput;
            if (specific is null || !specific.TryGetPropertyValue(key, out var node) || node is null)
            {
                continue;
            }

            var hookCommand = i < matching.Count ? matching[i].Command : string.Empty;
            if (node is not JsonObject obj)
            {
                this.LogNonObjectHookField(key, hookCommand, node.GetValueKind().ToString());
                continue;
            }

            var candidate = obj.ToJsonString();
            if (json is not null)
            {
                this.LogFieldOverride(key, json, candidate);
            }

            json = candidate;
            byHookCommand = hookCommand;
        }

        return (json, byHookCommand);
    }

    /// <summary>
    /// Merges <c>AgentResponse</c> hook outputs following last-writer-wins for both
    /// <c>displayContent</c> and <c>modifiedResponse</c>.
    /// </summary>
    private AgentResponseResult MergeAgentResponseOutputs(
        IReadOnlyList<UserHook> matching,
        IReadOnlyList<HookOutput> outputs)
    {
        if (outputs.Count == 0)
        {
            return AgentResponseResult.NoChange;
        }

        string? displayContent = null;
        string? modifiedResponse = null;
        string? lastHookCommand = null;

        for (var i = 0; i < outputs.Count; i++)
        {
            var specific = outputs[i].HookSpecificOutput;
            if (specific is null)
            {
                continue;
            }

            var hookCommand = i < matching.Count ? matching[i].Command : null;

            if (TryGetStringAllowEmpty(specific, "displayContent", out var dc))
            {
                if (matching[i].Mutates?.Any(m => string.Equals(m, "displayContent", StringComparison.OrdinalIgnoreCase)) != true)
                {
                    this.LogUndeclaredMutation(hookCommand ?? string.Empty, "displayContent");
                }

                displayContent = dc;
                lastHookCommand = hookCommand;
            }

            if (TryGetStringAllowEmpty(specific, "modifiedResponse", out var mr))
            {
                if (matching[i].Mutates?.Any(m => string.Equals(m, "modifiedResponse", StringComparison.OrdinalIgnoreCase)) != true)
                {
                    this.LogUndeclaredMutation(hookCommand ?? string.Empty, "modifiedResponse");
                }

                modifiedResponse = mr;
                lastHookCommand = hookCommand;
            }
        }

        if (displayContent is null && modifiedResponse is null)
        {
            return AgentResponseResult.NoChange;
        }

        return new AgentResponseResult(displayContent, modifiedResponse, lastHookCommand);
    }

    /// <summary>
    /// Merges <c>UserPromptSubmit</c> hook outputs following last-writer-wins
    /// for single-valued fields (logged), union for <c>deniedTools</c>, intersection for
    /// <c>allowedTools</c> (null = no opinion, not an empty list), concatenation for
    /// <c>additionalContext</c> / <c>appendSystemPrompt</c>.
    /// </summary>
    /// <remarks>
    /// The <paramref name="pairs"/> parameter carries the hook alongside its output so that
    /// <c>AllowSystemPromptReplace</c> and <c>UnattendedDecision</c> can be read per hook.
    /// </remarks>
    private UserPromptSubmitResult MergeUserPromptSubmitOutputs(
        IReadOnlyList<(UserHook Hook, HookOutput Output)> pairs)
    {
        if (pairs.Count == 0)
        {
            return UserPromptSubmitResult.Allow;
        }

        string? decision = null;
        var reasons = new List<string>();
        var additionalContexts = new List<string>();
        var appendSystemPrompts = new List<string>();
        var continueRun = true;
        string? stopReason = null;

        // Single-valued last-writer-wins fields for UserPromptSubmit.
        string? modifiedPrompt = null;
        string? modifiedPromptHookCommand = null;
        string? systemPrompt = null;
        string? toolChoice = null;
        string? modelOverride = null;
        string? effortOverride = null;

        // Tool list accumulators.
        List<string>? deniedTools = null;       // union — null until first hook contributes
        List<string>? allowedTools = null;       // intersection — null means "no restriction yet"
        var allowedHasValue = false;             // true once at least one hook expressed an allowedTools list

        foreach (var (hook, output) in pairs)
        {
            // Decision: strictest wins (allow < ask < deny < block).
            decision = StrictestDecision(decision, output.Decision);

            // Reasons: collected from blocking / denying hooks only.
            if (IsBlockingDecision(output.Decision) && !string.IsNullOrEmpty(output.Reason))
            {
                reasons.Add(output.Reason);
            }

            // Continue: false wins.
            if (!output.Continue)
            {
                continueRun = false;
            }

            // StopReason: last writer wins.
            if (output.StopReason is not null)
            {
                stopReason = output.StopReason;
            }

            // --- event-specific fields from hookSpecificOutput ---
            var specific = output.HookSpecificOutput;
            if (specific is null)
            {
                continue;
            }

            // additionalContext — concatenate in order.
            if (TryGetString(specific, "additionalContext", out var addCtx))
            {
                additionalContexts.Add(addCtx!);
            }

            // appendSystemPrompt — concatenate in order.
            if (TryGetString(specific, "appendSystemPrompt", out var appendSp))
            {
                appendSystemPrompts.Add(appendSp!);
            }

            // modifiedPrompt — last writer wins, logged.
            if (TryGetString(specific, "modifiedPrompt", out var mp))
            {
                if (modifiedPrompt is not null)
                {
                    this.LogFieldOverride("modifiedPrompt", modifiedPrompt, mp!);
                }

                modifiedPrompt = mp;
                modifiedPromptHookCommand = hook.Command;
            }

            // systemPrompt — last writer wins, only when AllowSystemPromptReplace.
            if (TryGetString(specific, "systemPrompt", out var sp))
            {
                if (!hook.AllowSystemPromptReplace)
                {
                    this.LogSystemPromptIgnored(hook.Command);
                }
                else
                {
                    if (systemPrompt is not null)
                    {
                        this.LogFieldOverride("systemPrompt", systemPrompt, sp!);
                    }

                    systemPrompt = sp;
                }
            }

            // toolChoice — last writer wins, logged.
            if (TryGetString(specific, "toolChoice", out var tc))
            {
                if (toolChoice is not null)
                {
                    this.LogFieldOverride("toolChoice", toolChoice, tc!);
                }

                toolChoice = tc;
            }

            // model — last writer wins, logged.
            if (TryGetString(specific, "model", out var m))
            {
                if (modelOverride is not null)
                {
                    this.LogFieldOverride("model", modelOverride, m!);
                }

                modelOverride = m;
            }

            // effort — last writer wins, logged.
            if (TryGetString(specific, "effort", out var eff))
            {
                if (effortOverride is not null)
                {
                    this.LogFieldOverride("effort", effortOverride, eff!);
                }

                effortOverride = eff;
            }

            // allowedTools — intersection (null from one hook = "no opinion").
            if (specific.TryGetPropertyValue("allowedTools", out var allowedNode)
                && allowedNode is JsonArray allowedArray)
            {
                var hookAllowed = ReadStringArray(allowedArray);
                if (!allowedHasValue)
                {
                    // First hook with an opinion: start with its list.
                    allowedTools = [.. hookAllowed];
                    allowedHasValue = true;
                }
                else
                {
                    // Subsequent hooks: intersect — a permissive hook cannot widen a restrictive one.
                    var hookSet = new HashSet<string>(hookAllowed, StringComparer.OrdinalIgnoreCase);
                    allowedTools = [.. (allowedTools ?? []).Where(t => hookSet.Contains(t))];
                }
            }

            // deniedTools — union.
            if (specific.TryGetPropertyValue("deniedTools", out var deniedNode)
                && deniedNode is JsonArray deniedArray)
            {
                var hookDenied = ReadStringArray(deniedArray);
                if (deniedTools is null)
                {
                    deniedTools = [.. hookDenied];
                }
                else
                {
                    foreach (var tool in hookDenied)
                    {
                        if (!deniedTools.Any(t => string.Equals(t, tool, StringComparison.OrdinalIgnoreCase)))
                        {
                            deniedTools.Add(tool);
                        }
                    }
                }
            }
        }

        // Resolve ask → unattended decision (§8.2).
        // ask requires an answerer; at this seam there is no interactive answerer wired.
        // Resolve via the hook's UnattendedDecision (per-hook, strictest wins across all
        // hooks that contributed ask). An interactive path is deliberately deferred — this
        // is the §8.2 rule.
        if (string.Equals(decision, "ask", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ResolveUnattendedAsk(pairs);
            this.LogAskResolvedUnattended(resolved);
            decision = resolved ? "allow" : "block";
            if (!resolved)
            {
                reasons.Add("ask resolved as deny (unattended — no interactive answerer)");
            }
        }

        if (!continueRun || IsBlockingDecision(decision))
        {
            var blockReason = reasons.Count > 0
                ? string.Join("\n\n", reasons)
                : stopReason ?? "blocked by hook";
            return new UserPromptSubmitResult { Block = true, Reason = blockReason };
        }

        // Build the TurnShape from merged fields.
        var shape = BuildTurnShape(systemPrompt, appendSystemPrompts, allowedTools, allowedHasValue, deniedTools, toolChoice, modelOverride, effortOverride);

        return new UserPromptSubmitResult
        {
            Block = false,
            ModifiedPrompt = modifiedPrompt,
            ModifiedByHookCommand = modifiedPromptHookCommand,
            AdditionalContext = additionalContexts.Count > 0 ? string.Join("\n\n", additionalContexts) : null,
            Shape = shape?.IsEmpty == false ? shape : null,
        };
    }

    private static TurnShape? BuildTurnShape(
        string? systemPrompt,
        List<string> appendSystemPrompts,
        List<string>? allowedTools,
        bool allowedHasValue,
        List<string>? deniedTools,
        string? toolChoice,
        string? model,
        string? effort)
    {
        var appendSp = appendSystemPrompts.Count > 0 ? string.Join("\n\n", appendSystemPrompts) : null;

        if (systemPrompt is null
            && appendSp is null
            && !allowedHasValue
            && deniedTools is null
            && toolChoice is null
            && model is null
            && effort is null)
        {
            return null;
        }

        return new TurnShape
        {
            SystemPrompt = systemPrompt,
            AppendSystemPrompt = appendSp,
            AllowedTools = allowedHasValue ? allowedTools?.AsReadOnly() : null,
            DeniedTools = deniedTools?.AsReadOnly(),
            ToolChoice = toolChoice,
            Model = model,
            Effort = effort,
        };
    }

    /// <summary>
    /// Resolves an <c>ask</c> decision via each contributing hook's <c>UnattendedDecision</c>.
    /// Returns <see langword="true"/> (allow) only when every hook that contributed <c>ask</c>
    /// has <c>UnattendedDecision == "allow"</c>. Any hook with <c>deny</c> or the default (null)
    /// resolves to <see langword="false"/> (deny/block), because deny is the safer default.
    /// </summary>
    private static bool ResolveUnattendedAsk(IReadOnlyList<(UserHook Hook, HookOutput Output)> pairs)
    {
        foreach (var (hook, output) in pairs)
        {
            if (string.Equals(output.Decision, "ask", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(hook.UnattendedDecision, "allow", StringComparison.OrdinalIgnoreCase))
                {
                    return false; // deny (or null default = deny)
                }
            }
        }

        return true; // all ask-returning hooks opted into allow
    }

    private static bool TryGetString(JsonObject obj, string key, out string? value)
    {
        if (obj.TryGetPropertyValue(key, out var node)
            && node is JsonValue jv
            && jv.TryGetValue<string>(out var s)
            && !string.IsNullOrWhiteSpace(s))
        {
            value = s;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Like <see cref="TryGetString"/> but permits an empty string: succeeds whenever the property
    /// exists and its value is a JSON string (including <c>""</c>). Used for
    /// <c>displayContent</c> and <c>modifiedResponse</c> in <see cref="MergeAgentResponseOutputs"/>,
    /// where an empty string means "suppress this response entirely".
    /// </summary>
    private static bool TryGetStringAllowEmpty(JsonObject obj, string key, out string? value)
    {
        if (obj.TryGetPropertyValue(key, out var node)
            && node is JsonValue jv
            && jv.TryGetValue<string>(out var s))
        {
            value = s;
            return true;
        }

        value = null;
        return false;
    }

    private static IEnumerable<string> ReadStringArray(JsonArray array)
    {
        foreach (var item in array)
        {
            if (item is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                yield return s.Trim();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Payload builders
    // -------------------------------------------------------------------------

    private string BuildPrePayload(string toolName, string inputJson, int depth, string? taskId)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "PreToolUse", depth, taskId);
            writer.WriteString("tool", toolName);
            writer.WritePropertyName("input");
            WriteJsonOrString(writer, inputJson);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string BuildPostPayload(string toolName, string inputJson, string resultText, string? errorText, int depth, string? taskId)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "PostToolUse", depth, taskId);
            writer.WriteString("tool", toolName);
            writer.WritePropertyName("input");
            WriteJsonOrString(writer, inputJson);
            writer.WriteString("result", resultText);

            // Gemini's shape: one event for success and failure, with an optional error field
            // carrying the failure text alongside the result.
            if (errorText is not null)
            {
                writer.WriteString("error", errorText);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string BuildPermissionRequestPayload(
        string toolName,
        string inputJson,
        string permissionMode,
        string? matchedRule,
        int depth,
        string? taskId)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "PermissionRequest", depth, taskId);
            writer.WriteString("tool", toolName);
            writer.WritePropertyName("input");
            WriteJsonOrString(writer, inputJson);
            writer.WriteString("permissionMode", permissionMode);

            // Always written (null when nothing matched) so a hook can rely on the key existing.
            if (matchedRule is null)
            {
                writer.WriteNull("matchedRule");
            }
            else
            {
                writer.WriteString("matchedRule", matchedRule);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string BuildStopPayload(int depth, string? taskId)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "Stop", depth, taskId);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string BuildStopWithOutcomePayload(
        string? stopReason,
        int iterations,
        int continuationCount,
        bool stopHookActive,
        int depth,
        string? taskId)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "Stop", depth, taskId);
            if (stopReason is not null)
            {
                writer.WriteString("stopReason", stopReason);
            }

            writer.WriteNumber("iterations", iterations);
            writer.WriteNumber("continuationCount", continuationCount);
            writer.WriteBoolean("stopHookActive", stopHookActive);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string BuildAgentResponsePayload(
        string response,
        string? stopReason,
        TokenUsage usage,
        long durationMs,
        int depth,
        string? taskId)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "AgentResponse", depth, taskId);
            writer.WriteString("response", response);
            if (stopReason is not null)
            {
                writer.WriteString("stopReason", stopReason);
            }

            writer.WriteStartObject("usage");
            writer.WriteNumber("inputTokens", usage.InputTokens);
            writer.WriteNumber("outputTokens", usage.OutputTokens);
            writer.WriteNumber("cacheReadTokens", usage.CacheReadTokens);
            writer.WriteNumber("cacheWrite5mTokens", usage.CacheWrite5mTokens);
            writer.WriteNumber("cacheWrite1hTokens", usage.CacheWrite1hTokens);
            writer.WriteEndObject();
            writer.WriteNumber("durationMs", durationMs);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string BuildUserPromptSubmitPayload(
        string prompt,
        IReadOnlyList<string> attachments,
        int historyLength,
        string model,
        string permissionMode,
        int depth,
        string? taskId)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "UserPromptSubmit", depth, taskId);
            writer.WriteString("prompt", prompt);
            writer.WriteStartArray("attachments");
            foreach (var kind in attachments)
            {
                writer.WriteStringValue(kind);
            }

            writer.WriteEndArray();
            writer.WriteNumber("historyLength", historyLength);
            writer.WriteString("model", model);
            writer.WriteString("permissionMode", permissionMode);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string BuildSessionStartPayload(
        string source,
        string model,
        string permissionMode,
        string? transcriptPath,
        string? resumedFrom)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "SessionStart", depth: 0, taskId: null);
            writer.WriteString("source", source);
            writer.WriteString("model", model);
            writer.WriteString("permissionMode", permissionMode);
            if (transcriptPath is not null)
            {
                writer.WriteString("transcriptPath", transcriptPath);
            }

            if (resumedFrom is not null)
            {
                writer.WriteString("resumedFrom", resumedFrom);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string BuildSessionEndPayload(
        string reason,
        long durationMs,
        int turnCount,
        TokenUsage usage,
        string? transcriptPath)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "SessionEnd", depth: 0, taskId: null);
            writer.WriteString("reason", reason);
            writer.WriteNumber("durationMs", durationMs);
            writer.WriteNumber("turnCount", turnCount);
            writer.WriteStartObject("usage");
            writer.WriteNumber("inputTokens", usage.InputTokens);
            writer.WriteNumber("outputTokens", usage.OutputTokens);
            writer.WriteNumber("cacheReadTokens", usage.CacheReadTokens);
            writer.WriteNumber("cacheWriteTokens", usage.CacheWriteTokens);
            writer.WriteEndObject();
            if (transcriptPath is not null)
            {
                writer.WriteString("transcriptPath", transcriptPath);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string BuildNotificationPayload(string kind, string message, string? taskId)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "Notification", depth: 0, taskId: null);
            writer.WriteString("kind", kind);
            writer.WriteString("message", message);
            if (taskId is not null)
            {
                writer.WriteString("taskId", taskId);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static SessionStartResult ParseSessionStartOutputs(IReadOnlyList<HookOutput> outputs)
    {
        string? additionalContext = null;
        var appendSystemPrompts = new List<string>();
        string? initialUserMessage = null;

        foreach (var output in outputs)
        {
            var specific = output.HookSpecificOutput;
            if (specific is null)
            {
                continue;
            }

            // additionalContext — concatenate in order.
            if (TryGetString(specific, "additionalContext", out var ac))
            {
                additionalContext = additionalContext is null ? ac : additionalContext + "\n\n" + ac;
            }

            // appendSystemPrompt — concatenate in order.
            if (TryGetString(specific, "appendSystemPrompt", out var asp))
            {
                appendSystemPrompts.Add(asp!);
            }

            // initialUserMessage — last-writer-wins (§4 spec).
            if (TryGetString(specific, "initialUserMessage", out var ium))
            {
                initialUserMessage = ium;
            }
        }

        if (additionalContext is null && appendSystemPrompts.Count == 0 && initialUserMessage is null)
        {
            return SessionStartResult.Empty;
        }

        return new SessionStartResult
        {
            AdditionalContext = additionalContext,
            AppendSystemPrompt = appendSystemPrompts.Count > 0 ? string.Join("\n\n", appendSystemPrompts) : null,
            InitialUserMessage = initialUserMessage,
        };
    }

    private void WriteEnvelope(Utf8JsonWriter writer, string eventName, int depth, string? taskId)
    {
        writer.WriteString("event", eventName);
        if (this.context is not null)
        {
            writer.WriteString("sessionId", this.context.SessionId);
            writer.WriteString("cwd", this.context.Cwd);
        }

        writer.WriteString("timestamp", (this.clock?.Invoke() ?? DateTimeOffset.UtcNow).ToString("o"));
        writer.WriteNumber("depth", depth);
        if (taskId is not null)
        {
            writer.WriteString("taskId", taskId);
        }
    }

    private static void WriteJsonOrString(Utf8JsonWriter writer, string json)
    {
        var normalized = string.IsNullOrWhiteSpace(json) ? "{}" : json;
        try
        {
            using var doc = JsonDocument.Parse(normalized);
            doc.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(json);
        }
    }

    // -------------------------------------------------------------------------
    // Output cap and spillover
    // -------------------------------------------------------------------------

    private string ApplyCapWithSpill(string text, string eventName, string stream)
    {
        if (text.Length <= OutputCap)
        {
            return text;
        }

        string? spillFile = null;
        try
        {
            var dir = this.spillDirFactory?.Invoke()
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".coda",
                    "hook-output");
            Directory.CreateDirectory(dir);

            var n = System.Threading.Interlocked.Increment(ref this.spillCounter);
            var ts = (this.clock?.Invoke() ?? DateTimeOffset.UtcNow).ToString("yyyyMMddTHHmmssZ");
            var filename = $"{ts}-{eventName}-{n}-{stream}.txt";
            spillFile = Path.Combine(dir, filename);
            File.WriteAllText(spillFile, text, Encoding.UTF8);
        }
        catch
        {
            // A spill-write failure must never fail the hook — degrade to plain truncation.
        }

        var truncated = text[..OutputCap];
        return spillFile is not null
            ? truncated + $"\n[output truncated; full text written to {spillFile}]"
            : truncated + "\n[output truncated]";
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private IReadOnlyList<UserHook> GetMatchingHooks(string eventName, string? toolName)
    {
        var result = new List<UserHook>();
        foreach (var hook in this.hooks)
        {
            if (!string.Equals(hook.Event, eventName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (toolName is not null && !HookMatcher.Matches(hook.Matcher, toolName))
            {
                continue;
            }

            result.Add(hook);
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Logging (source-generated)
    // -------------------------------------------------------------------------

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "hook exited with code {exitCode} for event '{eventName}' (command: {command})")]
    private partial void LogHookNonZeroExit(string command, int exitCode, string eventName);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "hook timed out after {timeoutSeconds}s for event '{eventName}' (command: {command})")]
    private partial void LogHookTimeout(string command, int timeoutSeconds, string eventName);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "hook threw an exception for event '{eventName}' (command: {command})")]
    private partial void LogHookException(string command, string eventName, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "hook single-valued field '{field}' overridden; previous: '{previous}', new: '{newValue}'")]
    private partial void LogFieldOverride(string field, string previous, string newValue);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "UserPromptSubmit hook '{command}' returned systemPrompt but allowSystemPromptReplace is false — ignoring; set allowSystemPromptReplace:true on the hook definition to enable full replacement")]
    private partial void LogSystemPromptIgnored(string command);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "UserPromptSubmit hook returned ask with no interactive answerer; resolved unattended as {resolution} (§8.2)")]
    private partial void LogAskResolvedUnattended(bool resolution);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "hook '{command}' returned '{field}' of kind {valueKind}; only a JSON object is accepted — ignoring")]
    private partial void LogNonObjectHookField(string field, string command, string valueKind);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "AgentResponse hook '{command}' returned '{field}' but did not declare it in 'mutates'; buffering may be off and the raw response may already have streamed — add \"{field}\" to the hook's mutates list to enable buffered redaction")]
    private partial void LogUndeclaredMutation(string command, string field);
}
