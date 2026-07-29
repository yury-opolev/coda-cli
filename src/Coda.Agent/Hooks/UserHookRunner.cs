using Microsoft.Extensions.Logging;

namespace Coda.Agent.Hooks;

/// <summary>
/// Executes user-configured shell hooks at agent lifecycle events
/// (UserPromptSubmit, PreToolUse, PostToolUse, Stop).
/// </summary>
/// <remarks>
/// <para>
/// This class is a thin facade over <see cref="HookBus"/>. All ordering, merging,
/// exit-code interpretation, output parsing, and fail-open/fail-closed policy live
/// inside the bus, which is the independently unit-testable orchestrator.
/// </para>
/// <para>
/// Process execution is injectable via <paramref name="execOverride"/> for tests.
/// The override returns <c>(exitCode, stdout)</c>; stderr is treated as empty when
/// using the legacy 2-tuple override.  For tests that need to supply stderr, create
/// <see cref="HookBus"/> directly with a full <see cref="IHookExecutor"/> implementation.
/// </para>
/// <para>
/// Important behaviour change from the previous implementation: a broken or timed-out
/// <c>PreToolUse</c> hook now <strong>blocks</strong> (fail-closed), because a policy
/// gate that silently permits on error is no gate at all. <c>PostToolUse</c> and
/// <c>Stop</c> remain fail-open.
/// </para>
/// </remarks>
public sealed class UserHookRunner
{
    private readonly HookBus bus;

    /// <summary>
    /// Initialises the runner.
    /// </summary>
    /// <param name="hooks">All user-configured hooks for this session.</param>
    /// <param name="execOverride">
    /// Optional test seam: a delegate that simulates shell execution, returning
    /// <c>(exitCode, stdout)</c>. Stderr is treated as empty when this is used.
    /// Pass <see langword="null"/> to use the real OS shell.
    /// </param>
    /// <param name="context">
    /// Optional session-level envelope values written into every hook payload.
    /// When <see langword="null"/> the envelope fields are omitted from the payload
    /// (backward-compatible with callers that do not supply a context).
    /// </param>
    /// <param name="logger">Logger forwarded to the underlying <see cref="HookBus"/>.</param>
    /// <param name="httpHandler">Optional handler for <c>http</c>-type hooks.</param>
    /// <param name="promptHandler">Optional handler for <c>prompt</c>-type hooks.</param>
    /// <param name="agentHandler">Optional handler for <c>agent</c>-type hooks.</param>
    public UserHookRunner(
        IReadOnlyList<UserHook> hooks,
        Func<string, string, CancellationToken, Task<(int exitCode, string stdout)>>? execOverride = null,
        HookContext? context = null,
        ILogger? logger = null,
        IHookHandler? httpHandler = null,
        IHookHandler? promptHandler = null,
        IHookHandler? agentHandler = null,
        HookTrustGuard? trustGuard = null,
        HookRunLog? runLog = null)
    {
        ArgumentNullException.ThrowIfNull(hooks);

        IHookExecutor executor = execOverride is not null
            ? new LegacyExecAdapter(execOverride)
            : new ShellHookExecutor();

        this.bus = new HookBus(hooks, executor, context, logger: logger,
            httpHandler: httpHandler, promptHandler: promptHandler, agentHandler: agentHandler,
            trustGuard: trustGuard, runLog: runLog);
    }

    /// <summary>
    /// Internal constructor for tests: accepts a full <see cref="IHookExecutor"/> so tests can
    /// inject a capturing executor that returns stderr without spawning real processes.
    /// </summary>
    internal UserHookRunner(
        IReadOnlyList<UserHook> hooks,
        IHookExecutor executor,
        HookContext? context = null,
        ILogger? logger = null,
        IHookHandler? httpHandler = null,
        IHookHandler? promptHandler = null,
        IHookHandler? agentHandler = null,
        HookTrustGuard? trustGuard = null,
        HookRunLog? runLog = null)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(executor);
        this.bus = new HookBus(hooks, executor, context, logger: logger,
            httpHandler: httpHandler, promptHandler: promptHandler, agentHandler: agentHandler,
            trustGuard: trustGuard, runLog: runLog);
    }

    /// <summary>True when at least one <c>PreToolUse</c> hook is configured.</summary>
    public bool HasPreToolUse => this.bus.HasPreToolUse;

    /// <summary>True when at least one <c>PostToolUse</c> hook is configured.</summary>
    public bool HasPostToolUse => this.bus.HasPostToolUse;

    /// <summary>True when at least one <c>PermissionRequest</c> hook is configured.</summary>
    public bool HasPermissionRequest => this.bus.HasPermissionRequest;

    /// <summary>True when at least one <c>UserPromptSubmit</c> hook is configured.</summary>
    public bool HasUserPromptSubmit => this.bus.HasUserPromptSubmit;

    /// <summary>True when at least one <c>SessionStart</c> hook is configured.</summary>
    public bool HasSessionStart => this.bus.HasSessionStart;

    /// <summary>True when at least one <c>SessionEnd</c> hook is configured.</summary>
    public bool HasSessionEnd => this.bus.HasSessionEnd;

    /// <summary>True when at least one <c>Notification</c> hook is configured.</summary>
    public bool HasNotification => this.bus.HasNotification;

    /// <summary>True when at least one <c>Stop</c> hook is configured.</summary>
    public bool HasStop => this.bus.HasStop;

    /// <summary>True when at least one <c>AgentResponse</c> hook is configured.</summary>
    public bool HasAgentResponse => this.bus.HasAgentResponse;

    /// <summary>
    /// True when any configured <c>AgentResponse</c> hook declares <c>"displayContent"</c> or
    /// <c>"modifiedResponse"</c> in its <c>mutates</c> list. Consulted once at session start by
    /// the TUI to decide whether to buffer assistant text before display.
    /// </summary>
    public bool AnyHookMutatesDisplay => this.bus.AnyHookMutatesDisplay;

    /// <summary>True when at least one <c>SubagentStart</c> hook is configured.</summary>
    public bool HasSubagentStart => this.bus.HasSubagentStart;

    /// <summary>True when at least one <c>SubagentStop</c> hook is configured.</summary>
    public bool HasSubagentStop => this.bus.HasSubagentStop;

    /// <summary>True when at least one <c>PreCompact</c> hook is configured.</summary>
    public bool HasPreCompact => this.bus.HasPreCompact;

    /// <summary>True when at least one <c>PostCompact</c> hook is configured.</summary>
    public bool HasPostCompact => this.bus.HasPostCompact;

    /// <summary>
    /// Runs all matching <c>PreToolUse</c> hooks in order and returns the merged result.
    /// A hook that exits non-zero (or fails / times out) now <strong>blocks</strong> because
    /// <c>PreToolUse</c> defaults to fail-closed.
    /// </summary>
    /// <param name="toolName">The name of the tool about to be called.</param>
    /// <param name="inputJson">The tool's input as a JSON string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth for this invocation: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The task identifier for this invocation, or <see langword="null"/> for the main agent.</param>
    public Task<UserHookResult> RunPreToolUseAsync(
        string toolName,
        string inputJson,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null) =>
        this.bus.RunPreToolUseAsync(toolName, inputJson, ct, depth, taskId);

    /// <summary>
    /// Runs all matching <c>PostToolUse</c> hooks and returns the merged result. Exit codes and
    /// errors never fail the tool call (fail-open default). A hook may replace the reported result
    /// via <c>hookSpecificOutput.modifiedResult</c> or <c>decision:"block"</c>; the tool has
    /// already run either way.
    /// </summary>
    /// <param name="toolName">The name of the tool that was called.</param>
    /// <param name="inputJson">The tool's input as a JSON string.</param>
    /// <param name="toolResultText">The tool result text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth for this invocation: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The task identifier for this invocation, or <see langword="null"/> for the main agent.</param>
    /// <param name="errorText">The failure text when the tool call failed, or <see langword="null"/>.</param>
    public Task<PostToolUseResult> RunPostToolUseAsync(
        string toolName,
        string inputJson,
        string toolResultText,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null,
        string? errorText = null) =>
        this.bus.RunPostToolUseAsync(toolName, inputJson, toolResultText, ct, depth, taskId, errorText);

    /// <summary>
    /// Runs all matching <c>PermissionRequest</c> hooks, fired only when a tool actually needs
    /// interactive approval and only after <c>PreToolUse</c> passed. Policy: 10 s timeout,
    /// fail-closed — a broken hook denies.
    /// </summary>
    /// <param name="toolName">The name of the tool requesting approval.</param>
    /// <param name="inputJson">The tool's input as a JSON string.</param>
    /// <param name="permissionMode">The live permission mode (e.g. <c>"default"</c>).</param>
    /// <param name="matchedRule">The matching rule in <c>allow:rule</c>/<c>deny:rule</c> form, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth for this invocation: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The task identifier for this invocation, or <see langword="null"/> for the main agent.</param>
    public Task<PermissionRequestResult> RunPermissionRequestAsync(
        string toolName,
        string inputJson,
        string permissionMode,
        string? matchedRule,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null) =>
        this.bus.RunPermissionRequestAsync(toolName, inputJson, permissionMode, matchedRule, ct, depth, taskId);

    /// <summary>
    /// Runs all <c>Stop</c> hooks. Exit codes and errors are ignored (fail-open default).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth for this invocation: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The task identifier for this invocation, or <see langword="null"/> for the main agent.</param>
    public Task RunStopAsync(CancellationToken ct, int depth = 0, string? taskId = null) =>
        this.bus.RunStopAsync(ct, depth, taskId);

    /// <summary>
    /// Runs all <c>Stop</c> hooks with full blocking power, returning a
    /// <see cref="StopHookOutcome"/> that the agent loop can act on. A hook returning
    /// <c>decision:"block"</c> forces continuation and injects its reason into history.
    /// Policy: 10 s timeout, fail-open.
    /// </summary>
    public Task<StopHookOutcome> RunStopWithOutcomeAsync(
        string? stopReason,
        int iterations,
        int continuationCount,
        bool stopHookActive,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null) =>
        this.bus.RunStopWithOutcomeAsync(stopReason, iterations, continuationCount, stopHookActive, ct, depth, taskId);

    /// <summary>
    /// Runs all <c>AgentResponse</c> hooks after the assistant's final text is settled, before
    /// it is displayed or persisted. Policy: 10 s timeout, fail-open.
    /// </summary>
    public Task<AgentResponseResult> RunAgentResponseAsync(
        string response,
        string? stopReason,
        LlmClient.TokenUsage usage,
        long durationMs,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null) =>
        this.bus.RunAgentResponseAsync(response, stopReason, usage, durationMs, ct, depth, taskId);

    /// <summary>
    /// Runs all matching <c>UserPromptSubmit</c> hooks in order and returns the merged result.
    /// A broken or timed-out hook <strong>blocks</strong> (fail-closed) because this is a policy
    /// gate: silently permitting on error would defeat the purpose.
    /// </summary>
    /// <param name="prompt">Concatenated text of all text blocks in the user message.</param>
    /// <param name="attachments">Non-text content-block kinds (e.g. <c>"image"</c>), or empty.</param>
    /// <param name="historyLength">Number of messages in history before this turn is appended.</param>
    /// <param name="model">The model identifier for this turn.</param>
    /// <param name="permissionMode">The permission mode string for this turn (e.g. <c>"default"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The running task id, or <see langword="null"/> for the main agent.</param>
    public Task<UserPromptSubmitResult> RunUserPromptSubmitAsync(
        string prompt,
        IReadOnlyList<string> attachments,
        int historyLength,
        string model,
        string permissionMode,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null) =>
        this.bus.RunUserPromptSubmitAsync(prompt, attachments, historyLength, model, permissionMode, ct, depth, taskId);

    /// <summary>
    /// Runs all <c>SessionStart</c> hooks and returns the merged session-scoped outputs.
    /// Fail-open: a broken or timed-out hook returns <see cref="SessionStartResult.Empty"/>.
    /// </summary>
    public Task<SessionStartResult> RunSessionStartAsync(
        string source,
        string model,
        string permissionMode,
        string? transcriptPath,
        string? resumedFrom,
        CancellationToken ct) =>
        this.bus.RunSessionStartAsync(source, model, permissionMode, transcriptPath, resumedFrom, ct);

    /// <summary>
    /// Runs all <c>SessionEnd</c> hooks (observation-only). The caller must apply a hard 2 s
    /// deadline via <paramref name="ct"/> before awaiting.
    /// </summary>
    public Task RunSessionEndAsync(
        string reason,
        long durationMs,
        int turnCount,
        LlmClient.TokenUsage usage,
        string? transcriptPath,
        CancellationToken ct) =>
        this.bus.RunSessionEndAsync(reason, durationMs, turnCount, usage, transcriptPath, ct);

    /// <summary>
    /// Runs all <c>Notification</c> hooks (observation-only). Callers should fire-and-forget
    /// so notification latency never blocks the agent.
    /// </summary>
    public Task RunNotificationAsync(
        string kind,
        string message,
        string? taskId,
        CancellationToken ct) =>
        this.bus.RunNotificationAsync(kind, message, taskId, ct);

    /// <summary>
    /// Runs all <c>SubagentStart</c> hooks before the nested agent makes its first model call.
    /// Policy: 10 s, fail-closed — a broken hook must not let an unshaped subagent run.
    /// </summary>
    public Task<SubagentStartResult> RunSubagentStartAsync(
        string? parentTaskId,
        string taskId,
        int depth,
        string prompt,
        IReadOnlyList<string> toolset,
        TurnShape? parentToolRestriction,
        CancellationToken ct) =>
        this.bus.RunSubagentStartAsync(parentTaskId, taskId, depth, prompt, toolset, parentToolRestriction, ct);

    /// <summary>
    /// Runs all <c>SubagentStop</c> hooks after the nested agent finishes, before its result returns to the parent.
    /// Policy: 10 s, fail-open — a broken hook must not lose the completed subagent work.
    /// </summary>
    public Task<SubagentStopResult> RunSubagentStopAsync(
        string taskId,
        int depth,
        string result,
        LlmClient.TokenUsage usage,
        CancellationToken ct) =>
        this.bus.RunSubagentStopAsync(taskId, depth, result, usage, ct);

    /// <summary>
    /// Runs all <c>PreCompact</c> hooks before history compaction.
    /// Policy: 10 s, fail-open — a broken hook lets compaction proceed.
    /// </summary>
    public Task<PreCompactResult> RunPreCompactAsync(
        string trigger,
        int tokensBefore,
        int messageCount,
        string? instructions,
        int depth,
        string? taskId,
        CancellationToken ct) =>
        this.bus.RunPreCompactAsync(trigger, tokensBefore, messageCount, instructions, depth, taskId, ct);

    /// <summary>
    /// Runs all <c>PostCompact</c> hooks after history compaction, before the next model call.
    /// Policy: 10 s, fail-open — a broken hook leaves the compacted history unchanged.
    /// </summary>
    public Task<PostCompactResult> RunPostCompactAsync(
        int tokensBefore,
        int tokensAfter,
        int messageCount,
        string summary,
        int depth,
        string? taskId,
        CancellationToken ct) =>
        this.bus.RunPostCompactAsync(tokensBefore, tokensAfter, messageCount, summary, depth, taskId, ct);

    // -------------------------------------------------------------------------
    // Legacy 2-tuple exec adapter
    // -------------------------------------------------------------------------

    /// <summary>Exposed for testing: the underlying hook bus.</summary>
    internal HookBus BusForTest => this.bus;

    /// <summary>
    /// Adapts the legacy <c>(exitCode, stdout)</c> test-override delegate to the
    /// <see cref="IHookExecutor"/> interface, supplying an empty string for stderr.
    /// </summary>
    private sealed class LegacyExecAdapter : IHookExecutor
    {
        private readonly Func<string, string, CancellationToken, Task<(int exitCode, string stdout)>> func;

        public LegacyExecAdapter(
            Func<string, string, CancellationToken, Task<(int exitCode, string stdout)>> func)
        {
            this.func = func;
        }

        public async Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command,
            string payload,
            CancellationToken ct)
        {
            var (exitCode, stdout) = await this.func(command, payload, ct).ConfigureAwait(false);
            return (exitCode, stdout, string.Empty);
        }
    }
}
