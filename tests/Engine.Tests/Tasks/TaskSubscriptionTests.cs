using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskSubscriptionTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-sub", logRoot: null);

    [Fact]
    public void Subscribe_CapturesInitialSnapshot()
    {
        var mgr = NewManager();
        mgr.Register(TaskKind.Shell, "pre-existing", parentTaskId: null);
        var sub = mgr.Subscribe();
        Assert.Single(sub.InitialSnapshot);
        Assert.Equal("task-0001", sub.InitialSnapshot[0].Id);
    }

    [Fact]
    public void Register_PublishesCreatedChange()
    {
        var mgr = NewManager();
        var sub = mgr.Subscribe();
        var t = mgr.Register(TaskKind.Subagent, "new", parentTaskId: null);

        var (changes, resync) = sub.Drain();
        Assert.False(resync);
        Assert.Contains(changes, c => c.TaskId == t.Id && c.Kind == TaskChangeKind.Created);
    }

    [Fact]
    public void AppendOutput_PublishesOutputChange()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var sub = mgr.Subscribe();
        mgr.AppendOutput(t.Id, "hi");

        var (changes, _) = sub.Drain();
        Assert.Contains(changes, c => c.TaskId == t.Id && c.Kind == TaskChangeKind.Output);
    }

    [Fact]
    public void Complete_PublishesStatusChange()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var sub = mgr.Subscribe();
        mgr.Complete(t.Id, "done");

        var (changes, _) = sub.Drain();
        Assert.Contains(changes, c => c.TaskId == t.Id && c.Kind == TaskChangeKind.Status);
    }

    [Fact]
    public void Fail_PublishesStatusChange()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var sub = mgr.Subscribe();
        Assert.True(mgr.Fail(t.Id, "boom"));

        var (changes, _) = sub.Drain();
        Assert.Contains(changes, c => c.TaskId == t.Id && c.Kind == TaskChangeKind.Status);
    }

    [Fact]
    public void Stop_PublishesStatusChange()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        var sub = mgr.Subscribe();
        Assert.True(mgr.Stop(t.Id));

        var (changes, _) = sub.Drain();
        Assert.Contains(changes, c => c.TaskId == t.Id && c.Kind == TaskChangeKind.Status);
    }

    [Fact]
    public void ManagerTransition_OnTerminalTask_ReturnsFalseAndDoesNotPublish()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.True(mgr.Complete(t.Id, "ok"));
        var sub = mgr.Subscribe();

        Assert.False(mgr.Fail(t.Id, "late"));
        Assert.False(mgr.Stop(t.Id));

        var (changes, _) = sub.Drain();
        Assert.Empty(changes);
    }

    [Fact]
    public void ManagerTransition_UnknownId_ReturnsFalse()
    {
        var mgr = NewManager();
        Assert.False(mgr.Complete("task-9999", "x"));
        Assert.False(mgr.Fail("task-9999", "x"));
        Assert.False(mgr.Stop("task-9999"));
    }

    [Fact]
    public void Drain_ClearsPendingChanges()
    {
        var mgr = NewManager();
        var sub = mgr.Subscribe();
        mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        sub.Drain();
        var (changes, resync) = sub.Drain();
        Assert.Empty(changes);
        Assert.False(resync);
    }

    [Fact]
    public void Overflow_DropsOldestAndReportsResyncRequired()
    {
        var sub = new TaskSubscription(initialSnapshot: [], capacity: 2);
        sub.Post(new TaskChange("task-0001", 1, TaskChangeKind.Output));
        sub.Post(new TaskChange("task-0001", 2, TaskChangeKind.Output));
        sub.Post(new TaskChange("task-0001", 3, TaskChangeKind.Output)); // evicts version 1

        var (changes, resync) = sub.Drain();
        Assert.True(resync);
        Assert.Equal(2, changes.Count);
        Assert.Equal(2, changes[0].Version);
        Assert.Equal(3, changes[1].Version);
    }

    [Fact]
    public void Drain_ResetsResyncFlagAfterGap()
    {
        var sub = new TaskSubscription(initialSnapshot: [], capacity: 1);
        sub.Post(new TaskChange("task-0001", 1, TaskChangeKind.Output));
        sub.Post(new TaskChange("task-0001", 2, TaskChangeKind.Output)); // evicts 1
        var (_, resync1) = sub.Drain();
        Assert.True(resync1);

        sub.Post(new TaskChange("task-0001", 3, TaskChangeKind.Output));
        var (_, resync2) = sub.Drain();
        Assert.False(resync2);
    }

    [Fact]
    public async Task WaitAsync_CompletesWhenChangePosted()
    {
        var sub = new TaskSubscription(initialSnapshot: [], capacity: 8);
        var wait = sub.WaitAsync();
        Assert.False(wait.IsCompleted);
        sub.Post(new TaskChange("task-0001", 1, TaskChangeKind.Created));
        await wait; // should complete promptly
    }

    [Fact]
    public async Task WaitAsync_CompletesImmediatelyWhenChangeAlreadyPending()
    {
        var sub = new TaskSubscription(initialSnapshot: [], capacity: 8);
        sub.Post(new TaskChange("task-0001", 1, TaskChangeKind.Created));
        var wait = sub.WaitAsync();
        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    public async Task WaitAsync_CanceledToken_Throws()
    {
        var sub = new TaskSubscription(initialSnapshot: [], capacity: 8);
        using var cts = new CancellationTokenSource();
        var wait = sub.WaitAsync(cts.Token);
        Assert.False(wait.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public void SlowSubscriber_DoesNotBlockProducer()
    {
        var mgr = NewManager();
        var sub = mgr.Subscribe(capacity: 4);
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);

        // Never drains; producer keeps posting well past capacity.
        for (var i = 0; i < 1000; i++)
        {
            mgr.AppendOutput(t.Id, "x");
        }

        var (changes, resync) = sub.Drain();
        Assert.True(resync);
        Assert.True(changes.Count <= 4);
    }

    [Fact]
    public void Constructor_NonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TaskSubscription(initialSnapshot: [], capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TaskSubscription(initialSnapshot: [], capacity: -5));
    }

    [Fact]
    public void Constructor_NullSnapshot_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new TaskSubscription(initialSnapshot: null!, capacity: 4));
    }

    [Fact]
    public void InitialSnapshot_IsImmutableToLaterRegistrations()
    {
        var mgr = NewManager();
        mgr.Register(TaskKind.Shell, "first", parentTaskId: null);
        var sub = mgr.Subscribe();
        Assert.Single(sub.InitialSnapshot);

        // Registering more tasks must not mutate the captured snapshot.
        mgr.Register(TaskKind.Shell, "second", parentTaskId: null);
        mgr.Register(TaskKind.Shell, "third", parentTaskId: null);
        Assert.Single(sub.InitialSnapshot);
        Assert.Equal("task-0001", sub.InitialSnapshot[0].Id);
    }

    [Fact]
    public void Unsubscribe_StopsFurtherNotifications()
    {
        var mgr = NewManager();
        var sub = mgr.Subscribe();
        mgr.Unsubscribe(sub);

        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        mgr.AppendOutput(t.Id, "x");

        var (changes, _) = sub.Drain();
        Assert.Empty(changes);
    }

    [Fact]
    public void Dispose_UnsubscribesAndIgnoresLatePosts()
    {
        var mgr = NewManager();
        var sub = mgr.Subscribe();
        sub.Dispose();

        // Late publishes after dispose must be ignored, not throw or accumulate.
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        mgr.AppendOutput(t.Id, "x");
        sub.Post(new TaskChange("task-0001", 99, TaskChangeKind.Output));

        var (changes, resync) = sub.Drain();
        Assert.Empty(changes);
        Assert.False(resync);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var mgr = NewManager();
        var sub = mgr.Subscribe();
        sub.Dispose();
        sub.Dispose(); // must not throw
    }

    [Fact]
    public async Task Dispose_WakesPendingWaiter()
    {
        var sub = new TaskSubscription(initialSnapshot: [], capacity: 8);
        var wait = sub.WaitAsync();
        Assert.False(wait.IsCompleted);
        sub.Dispose();
        await wait; // disposing must release any waiter promptly
    }

    [Fact]
    public void Subscribe_RacingRegister_YieldsSnapshotXorCreated_NoDuplicateOrMissing()
    {
        // Repeatedly race a subscriber against a registration of the same task and
        // assert every subscriber observes the racing task EXACTLY once: either in its
        // initial snapshot OR as a Created change, never both and never neither.
        // Deliberately blocking waits with timeouts keep this concurrency test bounded.
#pragma warning disable xUnit1031
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var mgr = NewManager();
            using var ready = new Barrier(2);
            TaskSubscription? sub = null;
            string? createdId = null;

            var subscriber = Task.Run(() =>
            {
                ready.SignalAndWait();
                sub = mgr.Subscribe();
            });
            var register = Task.Run(() =>
            {
                ready.SignalAndWait();
                createdId = mgr.Register(TaskKind.Shell, "racing", parentTaskId: null).Id;
            });

            Task.WaitAll(subscriber, register);

            var inSnapshot = sub!.InitialSnapshot.Any(s => s.Id == createdId);
            var (changes, _) = sub.Drain();
            var inCreated = changes.Any(c => c.TaskId == createdId && c.Kind == TaskChangeKind.Created);

            Assert.True(
                inSnapshot ^ inCreated,
                $"iteration {iteration}: task {createdId} must appear exactly once " +
                $"(snapshot={inSnapshot}, created={inCreated}).");
        }
#pragma warning restore xUnit1031
    }
}
