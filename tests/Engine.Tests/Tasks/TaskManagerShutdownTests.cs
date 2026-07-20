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
}
