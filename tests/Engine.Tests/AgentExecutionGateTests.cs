using Coda.Agent;

namespace Engine.Tests;

/// <summary>
/// Deterministic tests for <see cref="AgentExecutionGate"/>: the cooperative pause/execute
/// coordinator serializing pause leases and execution start/end through one lifecycle lock.
/// Async parking is asserted with bounded waits (a completion within a generous timeout) and
/// bounded non-completions (a short window in which a task must NOT complete), so the suite is
/// timing-robust rather than sleep-dependent.
/// </summary>
public sealed class AgentExecutionGateTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NonCompletionWindow = TimeSpan.FromMilliseconds(150);

    /// <summary>Awaits a task that is expected to complete promptly; fails fast if it hangs.</summary>
    private static Task ShouldComplete(Task task) => task.WaitAsync(CompletionTimeout);

    /// <summary>Asserts a task does NOT complete within a short window (it is parked).</summary>
    private static async Task ShouldStayParked(Task task)
    {
        var delay = Task.Delay(NonCompletionWindow);
        var first = await Task.WhenAny(task, delay);
        Assert.Same(delay, first);
    }

    [Fact]
    public async Task Idle_pause_request_is_reached_immediately()
    {
        var gate = new AgentExecutionGate();

        using var lease = gate.RequestPause();

        Assert.True(gate.IsPaused);
        Assert.False(gate.IsExecuting);
        await ShouldComplete(gate.WaitUntilPaused(CancellationToken.None));
    }

    [Fact]
    public async Task Active_request_is_reached_at_the_next_WaitIfPaused_boundary()
    {
        var gate = new AgentExecutionGate();
        using var execution = gate.BeginExecution();

        using var lease = gate.RequestPause();
        var reached = gate.WaitUntilPaused(CancellationToken.None);

        // Executing but no boundary hit yet: not reached.
        await ShouldStayParked(reached);

        var parked = gate.WaitIfPaused(CancellationToken.None);

        // The boundary marks reached, yet the turn stays parked until the lease is released.
        await ShouldComplete(reached);
        await ShouldStayParked(parked);

        lease.Dispose();
        await ShouldComplete(parked);
    }

    [Fact]
    public async Task Active_request_is_reached_when_the_turn_ends_before_any_boundary()
    {
        var gate = new AgentExecutionGate();
        var execution = gate.BeginExecution();

        using var lease = gate.RequestPause();
        var reached = gate.WaitUntilPaused(CancellationToken.None);
        await ShouldStayParked(reached);

        // The turn finishes without ever hitting WaitIfPaused: EndExecution satisfies "reached".
        execution.Dispose();

        await ShouldComplete(reached);
        Assert.False(gate.IsExecuting);
    }

    [Fact]
    public async Task All_leases_must_be_released_before_a_parked_turn_resumes()
    {
        var gate = new AgentExecutionGate();
        var l1 = gate.RequestPause();
        var l2 = gate.RequestPause();

        var parked = gate.WaitIfPaused(CancellationToken.None);
        await ShouldStayParked(parked);

        l1.Dispose();
        await ShouldStayParked(parked); // one lease still held

        l2.Dispose();
        await ShouldComplete(parked);
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public async Task Releasing_the_lease_resumes_and_re_arming_pauses_again()
    {
        var gate = new AgentExecutionGate();

        var lease = gate.RequestPause();
        var parked = gate.WaitIfPaused(CancellationToken.None);
        await ShouldStayParked(parked);
        lease.Dispose();
        await ShouldComplete(parked);

        // Not paused: a subsequent boundary passes straight through.
        await ShouldComplete(gate.WaitIfPaused(CancellationToken.None));

        // Re-arm: a fresh pause episode parks the next boundary again.
        var lease2 = gate.RequestPause();
        var parked2 = gate.WaitIfPaused(CancellationToken.None);
        await ShouldStayParked(parked2);
        lease2.Dispose();
        await ShouldComplete(parked2);
    }

    [Fact]
    public async Task WaitIfPaused_propagates_cancellation_while_parked()
    {
        var gate = new AgentExecutionGate();
        using var lease = gate.RequestPause();
        using var cts = new CancellationTokenSource();

        var parked = gate.WaitIfPaused(cts.Token);
        await ShouldStayParked(parked);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parked);
        // The gate is unaffected: still paused, and a fresh boundary still parks.
        Assert.True(gate.IsPaused);
    }

    [Fact]
    public async Task Turn_started_under_an_idle_pause_parks_at_its_first_boundary()
    {
        var gate = new AgentExecutionGate();

        // Pause requested while idle → reached immediately.
        var lease = gate.RequestPause();
        await ShouldComplete(gate.WaitUntilPaused(CancellationToken.None));

        // A newly starting turn must still park at its first boundary.
        using var execution = gate.BeginExecution();
        var parked = gate.WaitIfPaused(CancellationToken.None);
        await ShouldStayParked(parked);

        lease.Dispose();
        await ShouldComplete(parked);
    }

    [Fact]
    public async Task Concurrent_pause_and_start_always_park_at_the_first_boundary()
    {
        // Race RequestPause against BeginExecution repeatedly. Whichever wins the lock, the
        // invariant holds: while a lease is held the first boundary parks and only resumes once
        // the lease is released — there is no idle/start window that lets a boundary slip through.
        for (var i = 0; i < 100; i++)
        {
            var gate = new AgentExecutionGate();
            using var start = new Barrier(2);

            var pauseTask = Task.Run(() =>
            {
                start.SignalAndWait();
                return gate.RequestPause();
            });
            var execTask = Task.Run(() =>
            {
                start.SignalAndWait();
                return gate.BeginExecution();
            });

            var lease = await pauseTask;
            var execution = await execTask;

            Assert.True(gate.IsPaused);
            Assert.True(gate.IsExecuting);

            var parked = gate.WaitIfPaused(CancellationToken.None);
            await ShouldStayParked(parked);

            lease.Dispose();
            await ShouldComplete(parked);

            execution.Dispose();
            Assert.False(gate.IsExecuting);
        }
    }

    [Fact]
    public async Task Disposing_a_lease_twice_only_releases_once()
    {
        var gate = new AgentExecutionGate();

        var lease = gate.RequestPause();
        var parked = gate.WaitIfPaused(CancellationToken.None);

        lease.Dispose();
        lease.Dispose(); // idempotent: must not drive the ref-count negative

        await ShouldComplete(parked);
        Assert.False(gate.IsPaused);

        // A subsequent pause still parks (proving the count did not underflow below zero).
        using var lease2 = gate.RequestPause();
        Assert.True(gate.IsPaused);
        await ShouldStayParked(gate.WaitIfPaused(CancellationToken.None));
    }

    [Fact]
    public void Disposing_an_execution_scope_twice_only_ends_once()
    {
        var gate = new AgentExecutionGate();

        var scope = gate.BeginExecution();
        Assert.True(gate.IsExecuting);

        scope.Dispose();
        scope.Dispose(); // idempotent

        Assert.False(gate.IsExecuting);

        // Nesting: two scopes are reference-counted; execution ends only when both close.
        var outer = gate.BeginExecution();
        var inner = gate.BeginExecution();
        Assert.True(gate.IsExecuting);
        inner.Dispose();
        Assert.True(gate.IsExecuting);
        outer.Dispose();
        Assert.False(gate.IsExecuting);
    }

    [Fact]
    public void IsPaused_and_IsExecuting_reflect_lease_and_scope_state()
    {
        var gate = new AgentExecutionGate();
        Assert.False(gate.IsPaused);
        Assert.False(gate.IsExecuting);

        var lease = gate.RequestPause();
        Assert.True(gate.IsPaused);

        using (gate.BeginExecution())
        {
            Assert.True(gate.IsExecuting);
        }

        Assert.False(gate.IsExecuting);
        lease.Dispose();
        Assert.False(gate.IsPaused);
    }
}
