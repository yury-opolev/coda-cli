using System.Diagnostics;
using Coda.Agent;
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskManagerShutdownTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-shutdown", logRoot: null);

    private static string SleepCommand(int seconds) =>
        OperatingSystem.IsWindows() ? $"Start-Sleep -Seconds {seconds}" : $"sleep {seconds}";

    private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.True(condition(), "condition was not met before the timeout.");
    }

    /// <summary>A subagent host that blocks until its cancellation token fires, then throws.</summary>
    private sealed class BlockingHost : ISubagentHost
    {
        public async Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var reg = cancellationToken.Register(() => tcs.TrySetResult());
            await tcs.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return "unreachable";
        }
    }

    [Fact]
    public async Task ShutdownAsync_CancelsRunningShellAndMarksTerminal()
    {
        var mgr = NewManager();
        var id = mgr.StartShellBackground(SleepCommand(30), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60));

        // Let the process start, then shut down within a small budget.
        await Task.Delay(200);
        await mgr.ShutdownAsync(TimeSpan.FromSeconds(5));

        var snap = mgr.Get(id);
        Assert.NotNull(snap);
        Assert.NotEqual(TaskRunStatus.Running, snap!.Status);
    }

    [Fact]
    public async Task ShutdownAsync_CancelsRunningBackgroundSubagent()
    {
        var mgr = NewManager();
        var id = mgr.StartSubagentBackground(new BlockingHost(), "general-purpose", "go", "desc", parentTaskId: null);

        await WaitUntil(() => mgr.Get(id) is { Status: TaskRunStatus.Running });
        await mgr.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(TaskRunStatus.Running, mgr.Get(id)!.Status);
    }

    [Fact]
    public async Task Register_AfterShutdown_Throws()
    {
        var mgr = NewManager();
        await mgr.ShutdownAsync(TimeSpan.FromSeconds(1));

        Assert.Throws<InvalidOperationException>(() => mgr.Register(TaskKind.Subagent, "s", parentTaskId: null));
    }

    [Fact]
    public async Task StartShellBackground_AfterShutdown_Throws()
    {
        var mgr = NewManager();
        await mgr.ShutdownAsync(TimeSpan.FromSeconds(1));

        Assert.Throws<InvalidOperationException>(
            () => mgr.StartShellBackground(SleepCommand(1), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task ShutdownAsync_UncooperativeTask_ForceStopsWithinBudget()
    {
        var mgr = NewManager();
        // A bare Register has no worker to observe cancellation, so it never terminates on its
        // own: shutdown must force-mark it terminal after the bounded budget elapses.
        var t = mgr.Register(TaskKind.Subagent, "stuck", parentTaskId: null);

        var sw = Stopwatch.StartNew();
        await mgr.ShutdownAsync(TimeSpan.FromMilliseconds(200));
        sw.Stop();

        Assert.NotEqual(TaskRunStatus.Running, mgr.Get(t.Id)!.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"shutdown took too long: {sw.Elapsed}");
    }

    [Fact]
    public async Task ShutdownAsync_WakesSubscriptionWaiters_AndClosesThem()
    {
        var mgr = NewManager();
        using var sub = mgr.Subscribe();
        var wait = sub.WaitAsync();

        await mgr.ShutdownAsync(TimeSpan.FromSeconds(1));

        // The waiter completes (does not hang) and the subscription is closed.
        Assert.True(wait.IsCompleted);
        Assert.True(sub.IsClosed);
    }

    [Fact]
    public async Task ShutdownAsync_IsIdempotent()
    {
        var mgr = NewManager();
        var id = mgr.StartShellBackground(SleepCommand(30), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60));
        await Task.Delay(200);

        await mgr.ShutdownAsync(TimeSpan.FromSeconds(5));
        // A second shutdown must be a safe no-op.
        await mgr.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(TaskRunStatus.Running, mgr.Get(id)!.Status);
    }

    [Fact]
    public void Remove_RunningTask_IsRejected()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        Assert.Equal(TaskActionResult.Rejected, mgr.Remove(t.Id));
    }

    [Fact]
    public void Remove_TerminalTask_Succeeds()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        mgr.Complete(t.Id, "done");

        Assert.Equal(TaskActionResult.Ok, mgr.Remove(t.Id));
        Assert.Null(mgr.Get(t.Id));
        Assert.Empty(mgr.List());
    }

    [Fact]
    public void Remove_UnknownId_ReturnsNotFound()
    {
        var mgr = NewManager();
        Assert.Equal(TaskActionResult.NotFound, mgr.Remove("task-9999"));
    }

    [Fact]
    public void Remove_TerminalTask_PublishesRemovedChange()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        mgr.Complete(t.Id, "done");

        using var sub = mgr.Subscribe();
        Assert.Equal(TaskActionResult.Ok, mgr.Remove(t.Id));

        var (changes, _) = sub.Drain();
        Assert.Contains(changes, c => c.TaskId == t.Id && c.Kind == TaskChangeKind.Removed);
    }

    [Fact]
    public async Task DisposeAsync_IsGraceful()
    {
        var mgr = NewManager();
        var id = mgr.StartShellBackground(SleepCommand(30), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60));
        await Task.Delay(200);

        await mgr.DisposeAsync();

        Assert.NotEqual(TaskRunStatus.Running, mgr.Get(id)!.Status);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var mgr = NewManager();
        mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);

        await mgr.DisposeAsync();
        await mgr.DisposeAsync();
    }

    // ---- Task 9.1: registration/shutdown atomicity ----

    [Fact]
    public async Task Register_WhenRegistrationWins_TaskIsIncludedAndDrivenTerminal()
    {
        var mgr = NewManager();

        // Order A: registration wins (happens before shutdown begins). Shutdown must then include
        // the task in its running snapshot and drive it to a terminal state — never leave it running.
        var t = mgr.Register(TaskKind.Subagent, "won", parentTaskId: null);
        await mgr.ShutdownAsync(TimeSpan.FromMilliseconds(200));

        Assert.NotEqual(TaskRunStatus.Running, mgr.Get(t.Id)!.Status);
    }

    [Fact]
    public void Register_WhenShutdownWinsUnderLock_Throws_AndLeavesRegistryUntouched()
    {
        var mgr = NewManager();

        // Order B: force the dangerous interleaving deterministically. The barrier fires while a
        // Register call is in flight but BEFORE it takes the registry lock; inside it we run a full
        // shutdown to completion (sets _shuttingDown/_disposed and snapshots). When Register then
        // takes the lock its under-lock recheck must observe shutdown and throw, so no id/task/log
        // is ever created after disposal.
        mgr.RegisterBarrier = () =>
        {
            mgr.RegisterBarrier = null; // fire exactly once
            mgr.ShutdownAsync(TimeSpan.Zero).GetAwaiter().GetResult();
        };

        Assert.Throws<InvalidOperationException>(
            () => mgr.Register(TaskKind.Shell, "raced", parentTaskId: null));

        // Nothing leaked into the registry: no task, no order entry, no log writer.
        Assert.Empty(mgr.List());
        Assert.Null(mgr.Get("task-0001"));
        Assert.False(mgr.HasLogWriter("task-0001"));
    }

    [Fact]
    public async Task Register_ConcurrentWithShutdown_NeverLeavesRunningTaskAndNeverStartsAfterDisposed()
    {
        // Genuine race: each iteration a Register and a Shutdown are released simultaneously by a
        // barrier. The invariant must hold every time — either registration is rejected, or its
        // task is included and driven terminal — with both orderings observed across the run.
        var registrationWon = 0;
        var shutdownWon = 0;

        for (var i = 0; i < 60; i++)
        {
            var mgr = NewManager();
            using var barrier = new Barrier(2);
            string? startedId = null;
            InvalidOperationException? rejected = null;
            var host = new BlockingHost();

            var reg = Task.Run(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    startedId = mgr.StartSubagentBackground(host, "general-purpose", "go", "desc", parentTaskId: null);
                }
                catch (InvalidOperationException ex)
                {
                    rejected = ex;
                }
            });

            var shut = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await mgr.ShutdownAsync(TimeSpan.FromMilliseconds(200));
            });

            await Task.WhenAll(reg, shut);

            if (rejected is not null)
            {
                shutdownWon++;
                // A rejected registration must leave nothing behind and never start a worker.
                Assert.Null(startedId);
            }
            else
            {
                registrationWon++;
                Assert.NotNull(startedId);
                // Registration won the lock, so shutdown must have cancelled it: it must be terminal.
                await WaitUntil(() => mgr.Get(startedId!) is not { Status: TaskRunStatus.Running });
                Assert.NotEqual(TaskRunStatus.Running, mgr.Get(startedId!)!.Status);
            }
        }

        Assert.True(registrationWon > 0, "expected some iterations where registration won the race");
        Assert.True(shutdownWon > 0, "expected some iterations where shutdown won the race");
    }

    // ---- Task 9.3: direct shell ownership — explicit tree-kill before the budget wait ----

    [Fact]
    public async Task ShutdownAsync_TreeKillsAttachedShellBeforeWaitingOnBudget()
    {
        var mgr = NewManager();

        // An uncooperative shell task: it never terminates on token cancellation alone. Only an
        // explicit tree-kill stops it. We model the kill's effect (the process dying, its worker
        // finishing) by transitioning the task terminal from inside the attached kill delegate.
        var t = mgr.Register(TaskKind.Shell, "uncooperative", parentTaskId: null);
        var killRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.AttachShellKill(() =>
        {
            killRequested.TrySetResult();
            mgr.Stop(t.Id);
        });

        // A long budget: if shutdown only cancelled the token and waited, it would block the full
        // 30s on this never-self-terminating task. Because it tree-kills BEFORE waiting, the task
        // goes terminal immediately and the wait short-circuits.
        var sw = Stopwatch.StartNew();
        await mgr.ShutdownAsync(TimeSpan.FromSeconds(30));
        sw.Stop();

        Assert.True(killRequested.Task.IsCompletedSuccessfully, "shutdown must request a tree-kill of the attached shell");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"kill-before-wait should short-circuit the budget; took {sw.Elapsed}");
        Assert.NotEqual(TaskRunStatus.Running, mgr.Get(t.Id)!.Status);
    }

    [Fact]
    public void DetachShellKill_ClearsHandle_SoLaterKillIsNoOp()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "detachable", parentTaskId: null);
        var kills = 0;
        t.AttachShellKill(() => kills++);

        // Detaching (as happens when the shell process is disposed) clears the handle so a
        // subsequent kill request is a safe no-op — no double-dispose of a dead process.
        t.DetachShellKill();
        t.KillAttachedShell();

        Assert.Equal(0, kills);
    }

    // ---- Task 9.4: clean Removed versions ----

    [Fact]
    public void Remove_TerminalTask_BumpsVersionAndPublishesContiguousRemoved_NoResyncForCurrentSubscriber()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        mgr.Complete(t.Id, "done");
        var terminalVersion = mgr.Get(t.Id)!.Version;

        // Subscribe AFTER the task is terminal: the subscriber is current at version N.
        using var sub = mgr.Subscribe();
        Assert.Equal(TaskActionResult.Ok, mgr.Remove(t.Id));

        var (changes, resync) = sub.Drain();
        var removed = Assert.Single(changes);
        Assert.Equal(TaskChangeKind.Removed, removed.Kind);
        // Removal atomically bumps the version to N+1 so the Removed change is contiguous for a
        // subscriber current at N — it must NOT resync merely because the task was removed.
        Assert.Equal(terminalVersion + 1, removed.Version);
        Assert.False(resync);
    }
}
