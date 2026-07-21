namespace Coda.Agent;

/// <summary>
/// Cooperative execution gate for the main agent: lets an outside actor (e.g. the TUI) request a
/// pause and be told when the agent has actually come to rest at a safe boundary, then resume it.
/// A single lifecycle lock serializes pause leases and execution start/end so there is no
/// idle/start time-of-check-to-time-of-use window — a pause requested while the agent is idle is
/// still honored by a turn that starts an instant later.
/// <para>
/// The contract is cooperative: the agent loop must call <see cref="WaitIfPaused"/> at each of its
/// iteration boundaries, and wrap a run in <see cref="BeginExecution"/>. The gate itself never
/// interrupts in-flight model or tool work; it only parks at the boundaries the loop offers.
/// </para>
/// <para>
/// Shutdown ownership: the gate deliberately has no "release every lease" escape hatch. A parked
/// loop is always unparked through the caller's <see cref="CancellationToken"/> (see
/// <see cref="WaitIfPaused"/>, which propagates cancellation), and the outside actor that took a
/// pause lease owns releasing it in its own <c>finally</c>. Session disposal cancels the run token,
/// so a lease owner that fails to release still cannot leave the loop parked. A blanket release
/// would undermine the reference counting that lets independent actors hold leases concurrently, so
/// it is intentionally omitted.
/// </para>
/// </summary>
public sealed class AgentExecutionGate
{
    private readonly object sync = new();

    // Number of outstanding pause leases. The agent is "paused" (boundaries park) while > 0.
    private int leaseCount;

    // Reference count of active execution scopes. The agent is "executing" while > 0.
    private int executionCount;

    // For the current pause episode (leaseCount transitioned 0 -> 1): whether the agent has
    // reached a paused state, either by parking at a boundary or by ending execution.
    private bool reached;

    // Completed when the current pause episode reaches a paused state; null when not paused.
    private TaskCompletionSource? reachedSource;

    // Completed when the current pause episode ends (leaseCount -> 0), releasing parked boundaries;
    // null when not paused.
    private TaskCompletionSource? resumeSource;

    /// <summary>Whether at least one pause lease is currently held.</summary>
    public bool IsPaused
    {
        get { lock (this.sync) { return this.leaseCount > 0; } }
    }

    /// <summary>Whether at least one execution scope is currently open.</summary>
    public bool IsExecuting
    {
        get { lock (this.sync) { return this.executionCount > 0; } }
    }

    /// <summary>
    /// Requests a pause, returning a reference-counted lease. The agent stays paused until every
    /// outstanding lease is disposed. Disposing a lease is idempotent — a second dispose is a
    /// no-op and never drives the count negative. If the agent is idle when the first lease is
    /// taken, the paused state is reached immediately.
    /// </summary>
    public IDisposable RequestPause()
    {
        lock (this.sync)
        {
            this.leaseCount++;
            if (this.leaseCount == 1)
            {
                // First lease of a new pause episode: arm the reached/resume signals.
                this.reached = false;
                this.reachedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                this.resumeSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                // Idle request: nothing is running, so the paused state is reached at once.
                if (this.executionCount == 0)
                {
                    this.reached = true;
                    this.reachedSource.SetResult();
                }
            }

            return new Lease(this);
        }
    }

    /// <summary>
    /// Completes when the agent has reached a paused state for the current pause episode: either a
    /// turn parked at a <see cref="WaitIfPaused"/> boundary, or the running turn ended via
    /// <see cref="BeginExecution"/> scope disposal — whichever happens first. An idle pause request
    /// is already reached, so this completes immediately. Completes immediately when no pause is
    /// active. Honors <paramref name="cancellationToken"/>.
    /// </summary>
    public Task WaitUntilPaused(CancellationToken cancellationToken = default)
    {
        lock (this.sync)
        {
            if (this.reachedSource is null || this.reached)
            {
                return cancellationToken.IsCancellationRequested
                    ? Task.FromCanceled(cancellationToken)
                    : Task.CompletedTask;
            }

            return this.reachedSource.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The cooperative pause boundary the agent loop calls at each iteration before doing any
    /// model or tool work. If a pause is active it marks the paused state as reached and parks
    /// until every lease is released; otherwise it returns immediately. Cancellation propagates to
    /// the returned task without disturbing the gate's pause state.
    /// </summary>
    public Task WaitIfPaused(CancellationToken cancellationToken = default)
    {
        lock (this.sync)
        {
            if (this.leaseCount == 0)
            {
                return cancellationToken.IsCancellationRequested
                    ? Task.FromCanceled(cancellationToken)
                    : Task.CompletedTask;
            }

            if (!this.reached)
            {
                this.reached = true;
                this.reachedSource?.TrySetResult();
            }

            // Park on the resume signal; a cancel unblocks the caller but leaves the gate paused.
            return this.resumeSource!.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Marks the start of an agent run, returning a reference-counted scope whose disposal marks
    /// the run's end. Disposal is idempotent. If a pause was requested while idle, a turn that
    /// begins here still parks at its first <see cref="WaitIfPaused"/> boundary — the shared lock
    /// closes the idle/start race. When the last scope closes while a pause is pending, the paused
    /// state is reached (the turn ended before offering a boundary).
    /// </summary>
    public IDisposable BeginExecution()
    {
        lock (this.sync)
        {
            this.executionCount++;
            return new ExecutionScope(this);
        }
    }

    private void ReleasePause()
    {
        lock (this.sync)
        {
            this.leaseCount--;
            if (this.leaseCount == 0)
            {
                // Last lease released: wake parked boundaries and disarm the episode. A still-pending
                // WaitUntilPaused waiter (the episode ended before any boundary or turn-end reached
                // it) must be completed here too, otherwise it would hang forever on the orphaned
                // reached-source we are about to clear.
                this.resumeSource?.TrySetResult();
                this.reachedSource?.TrySetResult();
                this.resumeSource = null;
                this.reachedSource = null;
                this.reached = false;
            }
        }
    }

    private void EndExecution()
    {
        lock (this.sync)
        {
            this.executionCount--;
            if (this.executionCount == 0 && this.leaseCount > 0 && !this.reached)
            {
                // The turn ended without ever offering a boundary: that satisfies "reached".
                this.reached = true;
                this.reachedSource?.TrySetResult();
            }
        }
    }

    /// <summary>A single pause lease; disposal decrements the count exactly once.</summary>
    private sealed class Lease(AgentExecutionGate gate) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                gate.ReleasePause();
            }
        }
    }

    /// <summary>A single execution scope; disposal decrements the count exactly once.</summary>
    private sealed class ExecutionScope(AgentExecutionGate gate) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                gate.EndExecution();
            }
        }
    }
}
