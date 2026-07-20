namespace Coda.Agent.Tasks;

/// <summary>
/// Graceful, bounded, idempotent teardown for the manager. <see cref="TaskManager"/> implements
/// both <see cref="IDisposable"/> (the hard synchronous teardown in the core partial, still used
/// by <see cref="Dispose"/>) and <see cref="IAsyncDisposable"/> (this graceful path). Declaring
/// <c>: IAsyncDisposable</c> here unions with the <c>: IDisposable</c> on the core declaration.
/// </summary>
public sealed partial class TaskManager : IAsyncDisposable
{
    /// <summary>Set once shutdown begins (under <see cref="_gate"/>, atomically with the running-task snapshot); <see cref="Register"/> rechecks it under the same lock to refuse new tasks.</summary>
    private volatile bool _shuttingDown;

    /// <summary>Guards <see cref="Dispose"/> so double disposal is a safe no-op.</summary>
    private bool _disposed;

    /// <summary>Default teardown budget. Also composed into <c>CodaSession</c>'s sync-dispose budget.</summary>
    public static readonly TimeSpan DefaultShutdownBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gracefully shuts the manager down: atomically stops accepting registrations and snapshots
    /// the running set under the registry lock (so a concurrent <see cref="Register"/> either
    /// commits before the snapshot and is included, or is rejected by its under-lock recheck),
    /// cancels every running task's token AND explicitly tree-kills every attached shell process
    /// BEFORE the wait (so an uncooperative shell that ignores cancellation is still torn down
    /// promptly), waits up to <paramref name="budget"/> for tasks to reach a terminal state on
    /// their own via each task's terminal completion signal, then force-marks any straggler
    /// <c>stopped</c> so a snapshot never shows a phantom running task, and finally runs the hard
    /// synchronous teardown (subscriptions closed and waiters woken, per-task resources released,
    /// logs flushed). Best-effort and idempotent — a second call is a safe no-op-ish pass. Never
    /// throws from a task/worker (their exceptions are observed internally). On return every task
    /// is terminal and every managed shell process has been tree-killed.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan budget)
    {
        // Set _shuttingDown and snapshot the running set in ONE critical section, under the same
        // lock Register commits under. This makes the register-vs-shutdown decision atomic: a
        // registration that commits before this lock is in the snapshot (and cancelled below); one
        // that arrives after sees _shuttingDown under the lock and is rejected — never a task that
        // shutdown misses, and never a worker/log starting after teardown.
        List<ManagedTask> running;
        lock (_gate)
        {
            _shuttingDown = true;
            running = _order.Where(t => t.Status == TaskRunStatus.Running).ToList();
        }

        // Cancel every running task's token AND explicitly tree-kill its attached shell process (if
        // any) before waiting. Cancellation unblocks subagent loops and cooperative shell runners;
        // the explicit kill guarantees an uncooperative shell that ignores the token is still
        // killed here, not left to outlive the budget wait below.
        foreach (var t in running)
        {
            t.Cancel();
            t.KillAttachedShell();
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
        // Their process trees were already tree-killed above (explicit kill + token cancellation).
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
