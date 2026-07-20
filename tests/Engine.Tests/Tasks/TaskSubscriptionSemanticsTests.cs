using System.Reflection;
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

/// <summary>
/// Authoritative change-subscription semantics: exact per-change versions, version-gap
/// resync (gaps, duplicates, out-of-order, overflow), empty/terminal no-op appends, and
/// deterministic closure on manager dispose.
/// </summary>
public class TaskSubscriptionSemanticsTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-sem", logRoot: null);

    private static TaskSnapshot RunningSnapshot(string id, long version) =>
        new(id, ParentId: null, Depth: 1, TaskKind.Shell, "d",
            TaskRunStatus.Running, version, DateTimeOffset.UtcNow, EndedAt: null, "log", Result: null, Error: null);

    // ---- Finding 1: authoritative versions & gap detection ----

    [Fact]
    public void ConcurrentAppends_PublishExactContiguousVersions()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        const int n = 300;
        var sub = mgr.Subscribe(capacity: n + 16);

        Parallel.For(0, n, _ => mgr.AppendOutput(t.Id, "x"));

        var (changes, _) = sub.Drain();
        var versions = changes
            .Where(c => c.Kind == TaskChangeKind.Output)
            .Select(c => c.Version)
            .OrderBy(v => v)
            .ToList();

        // Every append must publish its own exact version: 1..n, each exactly once. A
        // re-read of the live version would produce duplicates and gaps under contention.
        Assert.Equal(Enumerable.Range(1, n).Select(i => (long)i), versions);
    }

    // Deliberately blocking waits with a bounded workload keep this concurrency test simple.
#pragma warning disable xUnit1031
    [Fact]
    public void ConcurrentAppendAndComplete_PublishDistinctExactVersions()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var sub = mgr.Subscribe(capacity: 4096);
        const int n = 200;

        var appender = Task.Run(() => Parallel.For(0, n, _ => mgr.AppendOutput(t.Id, "x")));
        var completer = Task.Run(() =>
        {
            Thread.Yield();
            mgr.Complete(t.Id, "done");
        });
        Task.WaitAll(appender, completer);

        var (changes, _) = sub.Drain();
        var versions = changes.Select(c => c.Version).ToList();

        // A racing status transition must not reuse a version already assigned to an
        // output append; every published version is exact and therefore distinct.
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }
#pragma warning restore xUnit1031

    [Fact]
    public void Post_ContiguousVersionsFromSnapshot_NoResync()
    {
        var sub = new TaskSubscription(new[] { RunningSnapshot("task-0001", 2) }, capacity: 8);
        sub.Post(new TaskChange("task-0001", 3, TaskChangeKind.Output));
        sub.Post(new TaskChange("task-0001", 4, TaskChangeKind.Status));

        var (_, resync) = sub.Drain();
        Assert.False(resync);
    }

    [Fact]
    public void Post_VersionGap_SetsResyncRequired()
    {
        var sub = new TaskSubscription(new[] { RunningSnapshot("task-0001", 0) }, capacity: 8);
        sub.Post(new TaskChange("task-0001", 1, TaskChangeKind.Output));
        var (_, r1) = sub.Drain();
        Assert.False(r1);

        sub.Post(new TaskChange("task-0001", 3, TaskChangeKind.Output)); // skipped version 2
        var (_, r2) = sub.Drain();
        Assert.True(r2);
    }

    [Fact]
    public void Post_DuplicateVersion_SetsResyncRequired()
    {
        var sub = new TaskSubscription(new[] { RunningSnapshot("task-0001", 0) }, capacity: 8);
        sub.Post(new TaskChange("task-0001", 1, TaskChangeKind.Output));
        var (_, r1) = sub.Drain();
        Assert.False(r1);

        sub.Post(new TaskChange("task-0001", 1, TaskChangeKind.Output)); // duplicate
        var (_, r2) = sub.Drain();
        Assert.True(r2);
    }

    [Fact]
    public void Post_OutOfOrderVersion_SetsResyncRequired()
    {
        var sub = new TaskSubscription(new[] { RunningSnapshot("task-0001", 5) }, capacity: 8);
        sub.Post(new TaskChange("task-0001", 4, TaskChangeKind.Output)); // older than snapshot
        var (_, resync) = sub.Drain();
        Assert.True(resync);
    }

    [Fact]
    public void Post_FirstChangeForUnknownTask_SetsResyncRequired()
    {
        var sub = new TaskSubscription(initialSnapshot: [], capacity: 8);
        // An Output arriving before any Created/snapshot means we missed the task's birth.
        sub.Post(new TaskChange("task-0001", 5, TaskChangeKind.Output));
        var (_, resync) = sub.Drain();
        Assert.True(resync);
    }

    [Fact]
    public void Post_CreatedThenContiguous_NoResync()
    {
        var sub = new TaskSubscription(initialSnapshot: [], capacity: 8);
        sub.Post(new TaskChange("task-0001", 0, TaskChangeKind.Created));
        sub.Post(new TaskChange("task-0001", 1, TaskChangeKind.Output));
        sub.Post(new TaskChange("task-0001", 2, TaskChangeKind.Status));

        var (_, resync) = sub.Drain();
        Assert.False(resync);
    }

    [Fact]
    public void Post_DuplicateCreatedForKnownTask_SetsResyncRequired()
    {
        var sub = new TaskSubscription(new[] { RunningSnapshot("task-0001", 0) }, capacity: 8);
        sub.Post(new TaskChange("task-0001", 0, TaskChangeKind.Created)); // already known
        var (_, resync) = sub.Drain();
        Assert.True(resync);
    }

    // ---- Finding 2: empty/null AppendOutput is a complete no-op ----

    [Fact]
    public void AppendOutput_EmptyOrNull_IsCompleteNoOp()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var sub = mgr.Subscribe();
        var before = t.ToSnapshot().Version;

        mgr.AppendOutput(t.Id, "");
        mgr.AppendOutput(t.Id, null!);

        Assert.Equal(before, t.ToSnapshot().Version);
        var (changes, resync) = sub.Drain();
        Assert.Empty(changes);
        Assert.False(resync);
    }

    [Fact]
    public async Task AppendOutput_Empty_DoesNotWakeWaiter()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var sub = mgr.Subscribe();

        var wait = sub.WaitAsync();
        mgr.AppendOutput(t.Id, "");
        Assert.False(wait.IsCompleted);

        mgr.AppendOutput(t.Id, "real");
        await wait;
    }

    [Fact]
    public void AppendOutput_Empty_WritesNothingToLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "coda-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new TaskManager(sessionId: "sess-empty", logRoot: root);
            var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
            mgr.AppendOutput(t.Id, "");
            mgr.Dispose();

            Assert.False(File.Exists(t.ToSnapshot().LogPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ---- Finding 4: append after terminal is a no-op ----

    [Fact]
    public void AppendOutput_AfterTerminal_IsNoOp()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.True(mgr.Complete(t.Id, "done"));
        var terminalVersion = t.ToSnapshot().Version;
        var sub = mgr.Subscribe();

        mgr.AppendOutput(t.Id, "late output");

        Assert.Equal(terminalVersion, t.ToSnapshot().Version);
        var (changes, resync) = sub.Drain();
        Assert.Empty(changes);
        Assert.False(resync);
    }

    [Fact]
    public void TryAppend_AfterTerminal_ReturnsNull()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.True(t.TryComplete("done"));
        var v = t.ToSnapshot().Version;

        Assert.Null(t.TryAppend("late"));
        Assert.Equal(v, t.ToSnapshot().Version);
    }

    [Fact]
    public void TryAppend_ReturnsExactAssignedVersion()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);

        Assert.Equal(1L, t.TryAppend("a"));
        Assert.Equal(2L, t.TryAppend("b"));
        Assert.Null(t.TryAppend(""));
        Assert.Equal(2, t.ToSnapshot().Version);
    }

    [Fact]
    public void TryComplete_OutVersion_ReturnsExactAssignedVersion()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        t.TryAppend("x"); // -> version 1

        Assert.True(t.TryComplete("done", out var version));
        Assert.Equal(2L, version);
        Assert.Equal(2, t.ToSnapshot().Version);
    }

    // ---- Finding 3: closure ----

    [Fact]
    public async Task ManagerDispose_WakesPendingWaiterAndClosesSubscription()
    {
        var mgr = NewManager();
        var sub = mgr.Subscribe();
        var wait = sub.WaitAsync();
        Assert.False(wait.IsCompleted);

        mgr.Dispose();

        await wait; // dispose must release the waiter promptly
        Assert.True(sub.IsClosed);
    }

    [Fact]
    public async Task WaitAsync_OnClosedSubscription_ReturnsCompleted()
    {
        var mgr = NewManager();
        var sub = mgr.Subscribe();
        sub.Dispose();
        Assert.True(sub.IsClosed);

        var wait = sub.WaitAsync();
        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    public void ManagerDispose_IsIdempotent()
    {
        var mgr = NewManager();
        mgr.Subscribe();
        mgr.Dispose();
        mgr.Dispose(); // must not throw
    }

    [Fact]
    public void ManagerDispose_IgnoresLatePosts()
    {
        var mgr = NewManager();
        var sub = mgr.Subscribe();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        mgr.Dispose();

        mgr.AppendOutput(t.Id, "late");

        var (changes, resync) = sub.Drain();
        Assert.Empty(changes);
        Assert.False(resync);
    }

    [Fact]
    public void Subscription_ConstructorAndPost_AreNotPublic()
    {
        var type = typeof(TaskSubscription);
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(type.GetMethod("Post", BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void TaskManager_Unsubscribe_IsNotPublic()
    {
        Assert.Null(typeof(TaskManager).GetMethod(
            "Unsubscribe", BindingFlags.Public | BindingFlags.Instance));
    }
}
