namespace Coda.Tui.Ui.Tasks;

/// <summary>
/// Pure, immutable browser state: the projected list, stable selection by task id, current view,
/// detail output source/scroll/auto-follow, a transient status message, and the steering draft.
/// No Terminal.Gui, no I/O — every transition returns a new instance so it is trivially testable.
/// </summary>
internal sealed record TaskBrowserState
{
    public static readonly TaskBrowserState Empty = new();

    public TaskBrowserView View { get; init; } = TaskBrowserView.List;
    public TaskListProjection Projection { get; init; } = TaskListProjection.Empty;
    public string? SelectedTaskId { get; init; }
    public TaskOutputSource OutputSource { get; init; } = TaskOutputSource.RecentRing;
    public int ScrollOffset { get; init; }
    public bool AutoFollow { get; init; } = true;
    public bool HasNewOutput { get; init; }
    public string? StatusMessage { get; init; }
    public string SteeringDraft { get; init; } = string.Empty;

    /// <summary>The currently selected row, resolved from <see cref="SelectedTaskId"/>, or null.</summary>
    public TaskListRow? Selected =>
        SelectedTaskId is null ? null : Projection.AllRows.FirstOrDefault(r => r.Task.Id == SelectedTaskId);

    /// <summary>
    /// Applies a fresh projection, keeping the selection stable by task id. If nothing is selected yet,
    /// selects the first row. If the selected task vanished while on the detail/steering page, returns to
    /// the list with a warning (a pruned/disappearing selected task cannot leave a stale detail open).
    /// </summary>
    public TaskBrowserState WithProjection(TaskListProjection projection)
    {
        var rows = projection.AllRows;
        var stillPresent = SelectedTaskId is not null && rows.Any(r => r.Task.Id == SelectedTaskId);
        var selected = stillPresent ? SelectedTaskId : rows.Count > 0 ? rows[0].Task.Id : null;

        var next = this with { Projection = projection, SelectedTaskId = selected };

        if (!stillPresent && View != TaskBrowserView.List && SelectedTaskId is not null)
        {
            return next with
            {
                View = TaskBrowserView.List,
                StatusMessage = "The selected task is no longer available.",
                SteeringDraft = string.Empty,
            };
        }

        return next;
    }

    public TaskBrowserState Select(string taskId) => this with { SelectedTaskId = taskId };

    public TaskBrowserState MoveSelection(int delta)
    {
        var rows = Projection.AllRows;
        if (rows.Count == 0) return this;
        var index = SelectedTaskId is null ? 0 : Math.Max(0, IndexOfSelected());
        var next = Math.Clamp(index + delta, 0, rows.Count - 1);
        return this with { SelectedTaskId = rows[next].Task.Id };
    }

    public TaskBrowserState MoveToStart() =>
        Projection.AllRows.Count == 0 ? this : this with { SelectedTaskId = Projection.AllRows[0].Task.Id };

    public TaskBrowserState MoveToEnd() =>
        Projection.AllRows.Count == 0 ? this : this with { SelectedTaskId = Projection.AllRows[^1].Task.Id };

    public TaskBrowserState OpenDetail() =>
        Selected is null ? this : this with
        {
            View = TaskBrowserView.Detail,
            OutputSource = TaskOutputSource.RecentRing,
            ScrollOffset = 0,
            AutoFollow = true,
            HasNewOutput = false,
            StatusMessage = null,
        };

    public TaskBrowserState ReturnToList() =>
        this with { View = TaskBrowserView.List, SteeringDraft = string.Empty };

    public TaskBrowserState ToggleOutputSource() => this with
    {
        OutputSource = OutputSource == TaskOutputSource.RecentRing
            ? TaskOutputSource.PersistentLog
            : TaskOutputSource.RecentRing,
    };

    public TaskBrowserState Scroll(int delta)
    {
        var offset = Math.Max(0, ScrollOffset - delta); // negative delta = scroll up = larger offset
        return this with { ScrollOffset = offset, AutoFollow = offset == 0 && AutoFollow };
    }

    public TaskBrowserState JumpToNewest() =>
        this with { ScrollOffset = 0, AutoFollow = true, HasNewOutput = false };

    /// <summary>Records new output; only surfaces the "new output" indicator while not auto-following.</summary>
    public TaskBrowserState MarkNewOutput() => AutoFollow ? this : this with { HasNewOutput = true };

    public TaskBrowserState WithStatus(string? message) => this with { StatusMessage = message };

    public TaskBrowserState BeginSteering() =>
        this with { View = TaskBrowserView.Steering, SteeringDraft = string.Empty };

    public TaskBrowserState AppendSteering(string text) => this with { SteeringDraft = SteeringDraft + text };

    public TaskBrowserState NewlineSteering() => this with { SteeringDraft = SteeringDraft + "\n" };

    public TaskBrowserState BackspaceSteering() =>
        SteeringDraft.Length == 0 ? this : this with { SteeringDraft = RemoveLastScalar(SteeringDraft) };

    /// <summary>
    /// Removes the final Unicode scalar (rune) from <paramref name="text"/>, not one UTF-16 code unit, so an
    /// astral emoji (a surrogate pair) is deleted whole and never leaves a lone, invalid surrogate. A
    /// combining mark is its own scalar and is removed on its own — one keypress deletes one rune, which is
    /// the accepted grapheme-cluster behaviour.
    /// </summary>
    private static string RemoveLastScalar(string text)
    {
        var last = text.Length - 1;
        return last > 0 && char.IsLowSurrogate(text[last]) && char.IsHighSurrogate(text[last - 1])
            ? text[..(last - 1)]
            : text[..last];
    }

    public TaskBrowserState CancelSteering() =>
        this with { View = TaskBrowserView.Detail, SteeringDraft = string.Empty };

    public TaskBrowserState CloseSteering() =>
        this with { View = TaskBrowserView.Detail, SteeringDraft = string.Empty };

    private int IndexOfSelected()
    {
        var rows = Projection.AllRows;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Task.Id == SelectedTaskId) return i;
        }

        return -1;
    }
}
