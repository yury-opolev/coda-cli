using System.Reflection;
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskManagerTests
{
    private static TaskManager NewManager() =>
        new(sessionId: "sess-test", logRoot: null);

    [Fact]
    public void Register_AssignsSequentialPaddedIds()
    {
        var mgr = NewManager();
        var a = mgr.Register(TaskKind.Subagent, "first", parentTaskId: null);
        var b = mgr.Register(TaskKind.Shell, "second", parentTaskId: null);

        Assert.Equal("task-0001", a.Id);
        Assert.Equal("task-0002", b.Id);
    }

    [Fact]
    public void Register_TopLevelTask_HasDepthOne()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "top", parentTaskId: null);
        Assert.Equal(1, t.Depth);
        Assert.Null(t.ToSnapshot().ParentId);
    }

    [Fact]
    public void Register_ChildTask_HasParentDepthPlusOne()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "p", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "c", parentTaskId: parent.Id);
        Assert.Equal(2, child.Depth);
        Assert.Equal(parent.Id, child.ToSnapshot().ParentId);
    }

    [Fact]
    public void Register_SubagentBeyondMaxDepth_Throws()
    {
        var mgr = NewManager();
        var d1 = mgr.Register(TaskKind.Subagent, "d1", parentTaskId: null);
        var d2 = mgr.Register(TaskKind.Subagent, "d2", parentTaskId: d1.Id);
        var ex = Assert.Throws<InvalidOperationException>(
            () => mgr.Register(TaskKind.Subagent, "d3", parentTaskId: d2.Id));
        Assert.Contains("depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_ShellBeyondMaxDepth_IsAllowed()
    {
        var mgr = NewManager();
        var d1 = mgr.Register(TaskKind.Subagent, "d1", parentTaskId: null);
        var d2 = mgr.Register(TaskKind.Subagent, "d2", parentTaskId: d1.Id);
        var shell = mgr.Register(TaskKind.Shell, "sh", parentTaskId: d2.Id);
        Assert.Equal(3, shell.Depth);
    }

    [Fact]
    public void Register_UnknownParent_Throws()
    {
        var mgr = NewManager();
        Assert.Throws<InvalidOperationException>(
            () => mgr.Register(TaskKind.Subagent, "x", parentTaskId: "task-9999"));
    }

    [Fact]
    public void NewTask_StartsRunningWithVersionZero()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var snap = t.ToSnapshot();
        Assert.Equal(TaskRunStatus.Running, snap.Status);
        Assert.Equal(0, snap.Version);
        Assert.Null(snap.EndedAt);
    }

    [Fact]
    public void TryComplete_MovesToCompletedAndBumpsVersion()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.True(t.TryComplete("done"));
        var snap = t.ToSnapshot();
        Assert.Equal(TaskRunStatus.Completed, snap.Status);
        Assert.Equal("done", snap.Result);
        Assert.Equal(1, snap.Version);
        Assert.NotNull(snap.EndedAt);
    }

    [Fact]
    public void TryStop_UsesStoppedTerminology()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        Assert.True(t.TryStop());
        Assert.Equal(TaskRunStatus.Stopped, t.ToSnapshot().Status);
    }

    [Fact]
    public void Transition_OnTerminalTask_ReturnsFalseAndKeepsState()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.True(t.TryComplete("ok"));
        Assert.False(t.TryFail("late"));
        Assert.False(t.TryStop());
        var snap = t.ToSnapshot();
        Assert.Equal(TaskRunStatus.Completed, snap.Status);
        Assert.Equal(1, snap.Version);
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var mgr = NewManager();
        Assert.Null(mgr.Get("task-0001"));
    }

    [Fact]
    public void List_ReturnsAllRegisteredTasksInOrder()
    {
        var mgr = NewManager();
        mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        mgr.Register(TaskKind.Shell, "b", parentTaskId: null);
        var ids = mgr.List().Select(s => s.Id).ToList();
        Assert.Equal(new[] { "task-0001", "task-0002" }, ids);
    }

    [Fact]
    public void CancelToken_IsSignalledOnStop()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        Assert.False(t.Token.IsCancellationRequested);
        t.Cancel();
        Assert.True(t.Token.IsCancellationRequested);
    }

    [Fact]
    public void Register_ConcurrentStarts_AssignUniqueSequentialIds()
    {
        var mgr = NewManager();

        // 100 parallel registrations must each get a distinct id and all appear in the list.
        Parallel.For(0, 100, _ => mgr.Register(TaskKind.Shell, "s", parentTaskId: null));

        var ids = mgr.List().Select(s => s.Id).ToList();
        Assert.Equal(100, ids.Count);
        Assert.Equal(100, ids.Distinct().Count());
    }

    [Fact]
    public void TryFail_MovesToFailedWithErrorAndBumpsVersion()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.True(t.TryFail("boom"));
        var snap = t.ToSnapshot();
        Assert.Equal(TaskRunStatus.Failed, snap.Status);
        Assert.Equal("boom", snap.Error);
        Assert.Null(snap.Result);
        Assert.Equal(1, snap.Version);
        Assert.NotNull(snap.EndedAt);
    }

    [Fact]
    public void Register_LogPath_IsComposedUnderSessionLogRoot()
    {
        var mgr = new TaskManager(sessionId: "sess-test", logRoot: "logroot");
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var expected = Path.Combine("logroot", "sess-test", t.Id + ".log");
        Assert.Equal(expected, t.ToSnapshot().LogPath);
    }

    [Fact]
    public void ManagedTask_IsNotPublic()
    {
        var type = typeof(ManagedTask);
        Assert.False(type.IsPublic, "ManagedTask must not be public; it is an internal lifecycle type.");
        Assert.True(type.IsNotPublic);
    }

    // Deliberately blocking waits with timeouts keep this concurrency regression bounded
    // and deterministic; async/await would not exercise the lock-scoped deadlock.
#pragma warning disable xUnit1031
    [Fact]
    public void Dispose_DisposesTasksOutsideManagerLock_NoDeadlockWithConcurrentReaders()
    {
        var mgr = NewManager();
        var task = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);

        using var callbackEntered = new ManualResetEventSlim(false);
        using var listCompleted = new ManualResetEventSlim(false);

        // This cancellation callback fires synchronously from ManagedTask.Dispose ->
        // _cts.Cancel(). It blocks until another thread completes a manager.List() call.
        // If TaskManager.Dispose holds the manager lock while cancelling, the reader
        // cannot acquire that lock and the two threads deadlock.
        using var registration = task.Token.Register(() =>
        {
            callbackEntered.Set();
            listCompleted.Wait(TimeSpan.FromSeconds(5));
        });

        var disposeTask = Task.Run(() => mgr.Dispose());

        Assert.True(
            callbackEntered.Wait(TimeSpan.FromSeconds(5)),
            "cancellation callback did not start");

        var readerTask = Task.Run(() =>
        {
            _ = mgr.List();
            listCompleted.Set();
        });

        Assert.True(
            readerTask.Wait(TimeSpan.FromSeconds(5)),
            "manager.List() blocked: Dispose held the manager lock while cancelling tasks.");
        Assert.True(
            disposeTask.Wait(TimeSpan.FromSeconds(5)),
            "Dispose did not complete.");
    }
#pragma warning restore xUnit1031
}
