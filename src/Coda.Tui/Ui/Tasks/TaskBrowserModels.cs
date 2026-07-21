using Coda.Agent.Tasks;

namespace Coda.Tui.Ui.Tasks;

/// <summary>Which page of the browser is showing.</summary>
internal enum TaskBrowserView
{
    List,
    Detail,
    Steering,
}

/// <summary>Where the detail page's output pane reads from.</summary>
internal enum TaskOutputSource
{
    /// <summary>The in-memory recent output ring (non-consuming TryPeek).</summary>
    RecentRing,

    /// <summary>The persistent on-disk log tail.</summary>
    PersistentLog,
}

/// <summary>A presentation-ready list row: a task plus its indentation in the active hierarchy.</summary>
internal sealed record TaskListRow(TaskSnapshot Task, int IndentDepth);

/// <summary>The grouped list projection: active tasks (hierarchy) first, recent terminal tasks below.</summary>
internal sealed record TaskListProjection(
    IReadOnlyList<TaskListRow> Active,
    IReadOnlyList<TaskListRow> Recent)
{
    public static readonly TaskListProjection Empty = new([], []);

    /// <summary>All rows in display order (active then recent) — the navigable sequence.</summary>
    public IReadOnlyList<TaskListRow> AllRows => [.. Active, .. Recent];
}
