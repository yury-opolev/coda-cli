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
}
