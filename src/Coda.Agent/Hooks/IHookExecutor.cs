namespace Coda.Agent.Hooks;

/// <summary>
/// Executes a single shell hook command and returns its exit code, standard output,
/// and standard error.
/// </summary>
/// <remarks>
/// Implementations are responsible only for process spawning and I/O collection.
/// Timeouts, failure policy, output caps, and output parsing are the caller's concern
/// (see <see cref="HookBus"/>).
/// </remarks>
public interface IHookExecutor
{
    /// <summary>
    /// Executes <paramref name="command"/> with <paramref name="payload"/> on stdin,
    /// honouring <paramref name="ct"/> for cancellation.
    /// </summary>
    /// <returns>
    /// A tuple of the process exit code, full stdout text, and full stderr text.
    /// Implementations should propagate <see cref="OperationCanceledException"/> so
    /// callers can distinguish hook-local timeout from caller-level cancellation.
    /// </returns>
    Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
        string command,
        string payload,
        CancellationToken ct);

    /// <summary>
    /// Provenance-aware overload. Implementations that spawn a real subprocess use
    /// <paramref name="scope"/> and <paramref name="fromPlugin"/> to decide whether coda's
    /// provider credentials are stripped from the child environment
    /// (see <see cref="HookCredentialScrubber"/>).
    /// </summary>
    /// <param name="command">The shell command to execute.</param>
    /// <param name="payload">The JSON payload written to the child's stdin.</param>
    /// <param name="scope">The settings file the hook was loaded from.</param>
    /// <param name="fromPlugin">Whether the hook was contributed by an installed plugin.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of the process exit code, full stdout text, and full stderr text.</returns>
    /// <remarks>
    /// The default implementation ignores provenance and forwards to the three-argument overload,
    /// so in-memory test doubles need not implement it.
    /// </remarks>
    Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
        string command,
        string payload,
        HookScope scope,
        bool fromPlugin,
        CancellationToken ct)
        => this.ExecAsync(command, payload, ct);
}
