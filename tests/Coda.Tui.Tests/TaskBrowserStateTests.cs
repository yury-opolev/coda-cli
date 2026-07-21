using Coda.Agent.Tasks;
using Coda.Tui.Ui.Tasks;
using Xunit;

namespace Coda.Tui.Tests;

public sealed class TaskBrowserStateTests
{
    private static TaskSnapshot Snap(string id, TaskRunStatus status = TaskRunStatus.Running) =>
        new(id, null, 1, TaskKind.Subagent, $"desc-{id}", status, TaskExecutionMode.Background, 1,
            DateTimeOffset.UnixEpoch, null, $"log-{id}", null, null);

    private static TaskListProjection Proj(params TaskSnapshot[] tasks) => TaskListProjector.Project(tasks);

    [Fact]
    public void WithProjection_SelectsFirstRow_WhenNoSelection()
    {
        var state = TaskBrowserState.Empty.WithProjection(Proj(Snap("task-0001"), Snap("task-0002")));
        Assert.Equal("task-0001", state.SelectedTaskId);
    }

    [Fact]
    public void Empty_HasNoSelection_AndListView()
    {
        Assert.Null(TaskBrowserState.Empty.SelectedTaskId);
        Assert.Equal(TaskBrowserView.List, TaskBrowserState.Empty.View);
        Assert.Null(TaskBrowserState.Empty.Selected);
    }

    [Fact]
    public void WithProjection_KeepsSelection_ByTaskId_AcrossReorder()
    {
        var state = TaskBrowserState.Empty
            .WithProjection(Proj(Snap("task-0001"), Snap("task-0002")))
            .Select("task-0002");

        // task-0001 finished and dropped to recent; task-0002 stays selected by id.
        var next = state.WithProjection(Proj(Snap("task-0001", TaskRunStatus.Completed), Snap("task-0002")));
        Assert.Equal("task-0002", next.SelectedTaskId);
    }

    [Fact]
    public void WithProjection_SelectedDisappears_FallsBackToFirstRow()
    {
        var state = TaskBrowserState.Empty
            .WithProjection(Proj(Snap("task-0001"), Snap("task-0002")))
            .Select("task-0002");

        var next = state.WithProjection(Proj(Snap("task-0001"), Snap("task-0003")));
        Assert.Equal("task-0001", next.SelectedTaskId);
    }

    [Fact]
    public void MoveSelection_ClampsWithinAllRows()
    {
        var state = TaskBrowserState.Empty.WithProjection(Proj(Snap("task-0001"), Snap("task-0002")));
        Assert.Equal("task-0002", state.MoveSelection(1).SelectedTaskId);
        Assert.Equal("task-0002", state.MoveSelection(1).MoveSelection(1).SelectedTaskId); // clamped at end
        Assert.Equal("task-0001", state.MoveSelection(-5).SelectedTaskId);                 // clamped at start
    }

    [Fact]
    public void MoveToStart_And_MoveToEnd_SelectBoundaryRows()
    {
        var state = TaskBrowserState.Empty.WithProjection(Proj(Snap("task-0001"), Snap("task-0002"), Snap("task-0003")));
        Assert.Equal("task-0003", state.MoveToEnd().SelectedTaskId);
        Assert.Equal("task-0001", state.MoveToEnd().MoveToStart().SelectedTaskId);
    }

    [Fact]
    public void OpenDetail_ThenSelectedPruned_ReturnsToListWithWarning()
    {
        var state = TaskBrowserState.Empty
            .WithProjection(Proj(Snap("task-0001")))
            .OpenDetail();
        Assert.Equal(TaskBrowserView.Detail, state.View);

        var next = state.WithProjection(TaskListProjection.Empty); // selected task disappeared
        Assert.Equal(TaskBrowserView.List, next.View);
        Assert.NotNull(next.StatusMessage);
        Assert.Contains("no longer", next.StatusMessage);
    }

    [Fact]
    public void OpenDetail_WithNoSelection_IsNoOp()
    {
        var state = TaskBrowserState.Empty.OpenDetail();
        Assert.Equal(TaskBrowserView.List, state.View);
    }

    [Fact]
    public void ReturnToList_FromDetail_ClearsSteeringDraft()
    {
        var state = TaskBrowserState.Empty.WithProjection(Proj(Snap("task-0001"))).OpenDetail();
        var back = state.ReturnToList();
        Assert.Equal(TaskBrowserView.List, back.View);
        Assert.Equal(string.Empty, back.SteeringDraft);
    }

    [Fact]
    public void ToggleOutputSource_FlipsBetweenRingAndLog()
    {
        var state = TaskBrowserState.Empty.WithProjection(Proj(Snap("task-0001"))).OpenDetail();
        Assert.Equal(TaskOutputSource.RecentRing, state.OutputSource);
        Assert.Equal(TaskOutputSource.PersistentLog, state.ToggleOutputSource().OutputSource);
        Assert.Equal(TaskOutputSource.RecentRing, state.ToggleOutputSource().ToggleOutputSource().OutputSource);
    }

    [Fact]
    public void Scrolling_Up_DisablesAutoFollow_And_JumpToNewest_RestoresIt()
    {
        var state = TaskBrowserState.Empty.WithProjection(Proj(Snap("task-0001"))).OpenDetail();
        Assert.True(state.AutoFollow);

        var scrolled = state.Scroll(-3);
        Assert.False(scrolled.AutoFollow);
        Assert.Equal(3, scrolled.ScrollOffset);

        var jumped = scrolled.MarkNewOutput().JumpToNewest();
        Assert.True(jumped.AutoFollow);
        Assert.Equal(0, jumped.ScrollOffset);
        Assert.False(jumped.HasNewOutput);
    }

    [Fact]
    public void Scroll_Down_CannotGoBelowZeroOffset()
    {
        var state = TaskBrowserState.Empty.WithProjection(Proj(Snap("task-0001"))).OpenDetail();
        var scrolled = state.Scroll(5); // scroll down past the newest → clamped at 0
        Assert.Equal(0, scrolled.ScrollOffset);
    }

    [Fact]
    public void MarkNewOutput_SetsIndicator_OnlyWhenNotAutoFollowing()
    {
        var following = TaskBrowserState.Empty.WithProjection(Proj(Snap("task-0001"))).OpenDetail();
        Assert.False(following.MarkNewOutput().HasNewOutput); // auto-follow consumes it silently

        var paused = following.Scroll(-1);
        Assert.True(paused.MarkNewOutput().HasNewOutput);     // scrolled up → show the indicator
    }

    [Fact]
    public void SteeringDraft_SupportsMultilineAndClear()
    {
        var state = TaskBrowserState.Empty.WithProjection(Proj(Snap("task-0001"))).OpenDetail().BeginSteering();
        Assert.Equal(TaskBrowserView.Steering, state.View);

        state = state.AppendSteering("line one").NewlineSteering().AppendSteering("line two");
        Assert.Equal("line one\nline two", state.SteeringDraft);

        state = state.BackspaceSteering();
        Assert.Equal("line one\nline tw", state.SteeringDraft);

        var cancelled = state.CancelSteering();
        Assert.Equal(TaskBrowserView.Detail, cancelled.View);
        Assert.Equal(string.Empty, cancelled.SteeringDraft);
    }

    [Fact]
    public void BackspaceSteering_OnEmptyDraft_IsNoOp()
    {
        var state = TaskBrowserState.Empty.WithProjection(Proj(Snap("task-0001"))).OpenDetail().BeginSteering();
        Assert.Equal(string.Empty, state.BackspaceSteering().SteeringDraft);
    }
}
