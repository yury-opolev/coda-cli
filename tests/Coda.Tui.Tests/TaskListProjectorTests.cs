using Coda.Agent.Tasks;
using Coda.Tui.Ui.Tasks;
using Xunit;

namespace Coda.Tui.Tests;

public sealed class TaskListProjectorTests
{
    private static TaskSnapshot Snap(
        string id, string? parent, TaskRunStatus status,
        TaskKind kind = TaskKind.Subagent, int depth = 1,
        DateTimeOffset? ended = null) =>
        new(id, parent, depth, kind, $"desc-{id}", status, TaskExecutionMode.Background, 1,
            DateTimeOffset.UnixEpoch, ended, $"log-{id}", null, null);

    [Fact]
    public void Active_RunningTasks_AreNestedByParent()
    {
        var tasks = new List<TaskSnapshot>
        {
            Snap("task-0001", null, TaskRunStatus.Running),
            Snap("task-0002", "task-0001", TaskRunStatus.Running, depth: 2),
            Snap("task-0003", null, TaskRunStatus.Running),
        };

        var p = TaskListProjector.Project(tasks);

        Assert.Equal(new[] { "task-0001", "task-0002", "task-0003" }, p.Active.Select(r => r.Task.Id));
        Assert.Equal(new[] { 0, 1, 0 }, p.Active.Select(r => r.IndentDepth));
        Assert.Empty(p.Recent);
    }

    [Fact]
    public void Terminal_Tasks_GoToRecent_NewestFirst()
    {
        var tasks = new List<TaskSnapshot>
        {
            Snap("task-0001", null, TaskRunStatus.Completed, ended: DateTimeOffset.UnixEpoch.AddMinutes(1)),
            Snap("task-0002", null, TaskRunStatus.Failed, ended: DateTimeOffset.UnixEpoch.AddMinutes(3)),
            Snap("task-0003", null, TaskRunStatus.Stopped, ended: DateTimeOffset.UnixEpoch.AddMinutes(2)),
        };

        var p = TaskListProjector.Project(tasks);

        Assert.Empty(p.Active);
        Assert.Equal(new[] { "task-0002", "task-0003", "task-0001" }, p.Recent.Select(r => r.Task.Id));
        Assert.All(p.Recent, r => Assert.Equal(0, r.IndentDepth));
    }

    [Fact]
    public void RunningChildOfTerminalParent_IsRootInActive()
    {
        var tasks = new List<TaskSnapshot>
        {
            Snap("task-0001", null, TaskRunStatus.Completed, ended: DateTimeOffset.UnixEpoch.AddMinutes(1)),
            Snap("task-0002", "task-0001", TaskRunStatus.Running, depth: 2),
        };

        var p = TaskListProjector.Project(tasks);

        var row = Assert.Single(p.Active);
        Assert.Equal("task-0002", row.Task.Id);
        Assert.Equal(0, row.IndentDepth); // parent not running → child is a root
        Assert.Single(p.Recent);
    }

    [Fact]
    public void OrphanRunningChild_WithMissingParent_IsPromotedToRoot()
    {
        var tasks = new List<TaskSnapshot>
        {
            // parent id never appears in the snapshot list (pruned) → child promoted safely.
            Snap("task-0002", "task-0missing", TaskRunStatus.Running, depth: 2),
        };

        var p = TaskListProjector.Project(tasks);

        var row = Assert.Single(p.Active);
        Assert.Equal("task-0002", row.Task.Id);
        Assert.Equal(0, row.IndentDepth);
    }

    [Fact]
    public void CyclicParentReferences_DoNotRecurseInfinitely()
    {
        var tasks = new List<TaskSnapshot>
        {
            // task-0001 ↔ task-0002 point at each other; neither is a "" root.
            Snap("task-0001", "task-0002", TaskRunStatus.Running),
            Snap("task-0002", "task-0001", TaskRunStatus.Running),
        };

        var p = TaskListProjector.Project(tasks);

        // Every running task must still be emitted exactly once, without hanging.
        Assert.Equal(2, p.Active.Count);
        Assert.Equal(new[] { "task-0001", "task-0002" }, p.Active.Select(r => r.Task.Id).OrderBy(x => x));
        Assert.Empty(p.Recent);
    }

    [Fact]
    public void DeepHierarchy_PreservesDepthAndSiblingOrder()
    {
        var tasks = new List<TaskSnapshot>
        {
            Snap("task-0001", null, TaskRunStatus.Running),
            Snap("task-0002", "task-0001", TaskRunStatus.Running),
            Snap("task-0003", "task-0002", TaskRunStatus.Running),
            Snap("task-0004", "task-0001", TaskRunStatus.Running),
        };

        var p = TaskListProjector.Project(tasks);

        Assert.Equal(new[] { "task-0001", "task-0002", "task-0003", "task-0004" }, p.Active.Select(r => r.Task.Id));
        Assert.Equal(new[] { 0, 1, 2, 1 }, p.Active.Select(r => r.IndentDepth));
    }

    [Fact]
    public void PreservesTaskMetadata_KindModeStatus()
    {
        var tasks = new List<TaskSnapshot>
        {
            Snap("task-0001", null, TaskRunStatus.Running, kind: TaskKind.Shell),
            Snap("task-0002", null, TaskRunStatus.Running, kind: TaskKind.Subagent),
        };

        var p = TaskListProjector.Project(tasks);

        Assert.Equal(TaskKind.Shell, p.Active[0].Task.Kind);
        Assert.Equal(TaskKind.Subagent, p.Active[1].Task.Kind);
        Assert.All(p.Active, r => Assert.Equal(TaskExecutionMode.Background, r.Task.Mode));
    }

    [Fact]
    public void EmptyInput_ProducesEmptyProjection()
    {
        var p = TaskListProjector.Project(Array.Empty<TaskSnapshot>());

        Assert.Empty(p.Active);
        Assert.Empty(p.Recent);
        Assert.Empty(p.AllRows);
    }

    [Fact]
    public void Recent_IsBounded_ByMaxRecent()
    {
        var tasks = Enumerable.Range(1, TaskListProjector.MaxRecent + 10)
            .Select(i => Snap($"task-{i:0000}", null, TaskRunStatus.Completed,
                ended: DateTimeOffset.UnixEpoch.AddMinutes(i)))
            .ToList();

        var p = TaskListProjector.Project(tasks);

        Assert.Equal(TaskListProjector.MaxRecent, p.Recent.Count);
    }
}
