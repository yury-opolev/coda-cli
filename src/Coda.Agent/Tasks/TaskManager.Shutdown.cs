namespace Coda.Agent.Tasks;

/// <summary>
/// Graceful, bounded, idempotent teardown for the manager. <see cref="TaskManager"/> implements
/// both <see cref="IDisposable"/> (the hard synchronous teardown in the core partial, still used
/// by <see cref="Dispose"/>) and <see cref="IAsyncDisposable"/> (this graceful path). Declaring
/// <c>: IAsyncDisposable</c> here unions with the <c>: IDisposable</c> on the core declaration.
/// </summary>
public sealed partial class TaskManager : IAsyncDisposable
{
    /// <summary>Set once shutdown begins; <see cref="Register"/> reads it to refuse new tasks.</summary>
    private volatile bool _shuttingDown;

    /// <summary>Guards <see cref="Dispose"/> so double disposal is a safe no-op.</summary>
    private bool _disposed;

    /// <summary>Default teardown budget matching <c>CodaSession.DisposeTimeout</c>.</summary>
    private static readonly TimeSpan DefaultShutdownBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gracefully shuts the manager down: atomically stops accepting registrations, cancels every
    /// running task (subagent loops observe the token; shell <c>RunToEndAsync</c> tree-kills its
    /// process on cancellation), waits up to <paramref name="budget"/> for them to reach a terminal
    /// state on their own via each task's terminal completion signal, then force-marks any
    /// straggler <c>stopped</c> so a snapshot never shows a phantom running task, and finally runs
    /// the hard synchronous teardown (subscriptions closed and waiters woken, per-task resources
    /// released, logs flushed). Best-effort and idempotent — a second call is a no-op-ish safe
    /// pass. Never throws from a task/worker (their exceptions are observed internally), so no
    /// unobserved exception escapes. On return every task is terminal and every process is dead.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan budget)
    {
        // Atomically refuse new registrations before enumerating so nothing new can slip in
        // between the snapshot and the cancellation below.
        _shuttingDown = true;

        List<ManagedTask> running;
        lock (_gate)
        {
            running = _order.Where(t => t.Status == TaskRunStatus.Running).ToList();
        }

        // Cancel every running task's token. This both unblocks subagent loops and drives shell
        // process-tree kills through ManagedShellProcess.RunToEndAsync's cancellation handling.
        foreach (var t in running)
        {
            t.Cancel();
        }

        // Wait, bounded, for tasks to reach a terminal state on their own. Using each task's
        // terminal completion (never-faulting) makes the wait deterministic instead of a poll and
        // captures foreground work active in other callers, not just Task.Run workers. WhenAll
        // over never-faulting completions cannot fault, so nothing is left unobserved.
        if (running.Count > 0)
        {
            var wait = budget < TimeSpan.Zero ? TimeSpan.Zero : budget;
            var all = Task.WhenAll(running.Select(t => t.Completion));
            await Task.WhenAny(all, Task.Delay(wait)).ConfigureAwait(false);
        }

        // Force-mark any straggler terminal so the snapshot never shows a phantom running task.
        // Their process trees were already tree-killed by the token cancellation above.
        foreach (var t in running)
        {
            if (t.Status == TaskRunStatus.Running)
            {
                Stop(t.Id);
            }
        }

        // Hard teardown: closes subscriptions (waking waiters), disposes tasks, flushes logs.
        // Idempotent via the _disposed guard, so a repeated shutdown stays safe.
        Dispose();
    }

    /// <summary>Graceful async disposal using the default teardown budget. Idempotent.</summary>
    public async ValueTask DisposeAsync() =>
        await ShutdownAsync(DefaultShutdownBudget).ConfigureAwait(false);
}
