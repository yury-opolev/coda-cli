using Microsoft.Extensions.Logging;

namespace Coda.Agent.Hooks;

/// <summary>
/// Executes user-configured shell hooks at agent lifecycle events
/// (PreToolUse, PostToolUse, Stop).
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
    public UserHookRunner(
        IReadOnlyList<UserHook> hooks,
        Func<string, string, CancellationToken, Task<(int exitCode, string stdout)>>? execOverride = null,
        HookContext? context = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hooks);

        IHookExecutor executor = execOverride is not null
            ? new LegacyExecAdapter(execOverride)
            : new ShellHookExecutor();

        this.bus = new HookBus(hooks, executor, context, logger: logger);
    }

    /// <summary>True when at least one <c>PreToolUse</c> hook is configured.</summary>
    public bool HasPreToolUse => this.bus.HasPreToolUse;

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
    /// Runs all matching <c>PostToolUse</c> hooks. Exit codes and errors are ignored
    /// (fail-open default — observation-only hooks must not interrupt tool execution).
    /// </summary>
    /// <param name="toolName">The name of the tool that was called.</param>
    /// <param name="inputJson">The tool's input as a JSON string.</param>
    /// <param name="toolResultText">The tool result text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth for this invocation: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The task identifier for this invocation, or <see langword="null"/> for the main agent.</param>
    public Task RunPostToolUseAsync(
        string toolName,
        string inputJson,
        string toolResultText,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null) =>
        this.bus.RunPostToolUseAsync(toolName, inputJson, toolResultText, ct, depth, taskId);

    /// <summary>
    /// Runs all <c>Stop</c> hooks. Exit codes and errors are ignored (fail-open default).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth for this invocation: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The task identifier for this invocation, or <see langword="null"/> for the main agent.</param>
    public Task RunStopAsync(CancellationToken ct, int depth = 0, string? taskId = null) =>
        this.bus.RunStopAsync(ct, depth, taskId);

    // -------------------------------------------------------------------------
    // Legacy 2-tuple exec adapter
    // -------------------------------------------------------------------------

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
