using Coda.Agent.Scheduling;
using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Ui.Schedule;

/// <summary>
/// The live services the schedule browser binds to: the schedule control surface and the
/// interactive prompt service. A factory-returned record passed as a provider delegate to the
/// shell constructor, so the browser is lazily constructed only once a session exists.
/// </summary>
internal sealed record ScheduleBrowserProvider(
    Func<IScheduleControl?> Control,
    IUiPromptService Prompts);

/// <summary>
/// Immutable state snapshot for the schedule browser. Mutated only inside
/// <see cref="ScheduleBrowserController"/>'s lock; the overlay always holds a reference-copied
/// snapshot so a concurrent pump cannot corrupt a render in progress.
/// </summary>
internal sealed record ScheduleBrowserState(
    IReadOnlyList<ScheduledTaskReadModel> Rows,
    string? SelectedId,
    string? StatusMessage,
    bool IsActionBusy)
{
    /// <summary>Empty initial state (zero rows, no selection, no status, not busy).</summary>
    public static readonly ScheduleBrowserState Empty =
        new([], null, null, false);

    /// <summary>Returns a copy with the rows replaced, preserving selection where possible.</summary>
    public ScheduleBrowserState WithRows(IReadOnlyList<ScheduledTaskReadModel> rows)
    {
        // Try to keep the same selected id; fall back to first row.
        var newSel = SelectedId is not null && rows.Any(r => r.Id == SelectedId)
            ? SelectedId
            : rows.Count > 0 ? rows[0].Id : null;
        return this with { Rows = rows, SelectedId = newSel };
    }

    /// <summary>Returns a copy with the selection moved by <paramref name="delta"/> (clamped to bounds).</summary>
    public ScheduleBrowserState WithSelectionMoved(int delta)
    {
        if (Rows.Count == 0) return this;

        var idx = Rows.FindIndex(r => r.Id == SelectedId);
        if (idx < 0) idx = 0;
        var next = Math.Clamp(idx + delta, 0, Rows.Count - 1);
        return this with { SelectedId = Rows[next].Id };
    }
}

file static class ListExtensions
{
    internal static int FindIndex<T>(this IReadOnlyList<T> list, Func<T, bool> predicate)
    {
        for (var i = 0; i < list.Count; i++)
            if (predicate(list[i])) return i;
        return -1;
    }
}
