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
}
