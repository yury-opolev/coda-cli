namespace Coda.Tui.Ui.Schedule;

/// <summary>A resolved key action within the <c>/schedule</c> browser.</summary>
internal enum ScheduleBrowserCommand
{
    /// <summary>No action.</summary>
    None,

    /// <summary>Close the overlay.</summary>
    Close,

    /// <summary>Move the selection up one row.</summary>
    MoveUp,

    /// <summary>Move the selection down one row.</summary>
    MoveDown,

    /// <summary>Move the selection up one page.</summary>
    PageUp,

    /// <summary>Move the selection down one page.</summary>
    PageDown,

    /// <summary>Move the selection to the first row.</summary>
    MoveToStart,

    /// <summary>Move the selection to the last row.</summary>
    MoveToEnd,

    /// <summary>Delete the selected schedule.</summary>
    DeleteSelected,

    /// <summary>Create a new schedule.</summary>
    CreateNew,

    /// <summary>Reload / refresh the schedule list.</summary>
    Reload,

    /// <summary>Enter type-to-filter mode.</summary>
    Filter,
}

/// <summary>
/// Maps a raw Terminal.Gui <see cref="Key"/> to a <see cref="ScheduleBrowserCommand"/>.
/// Previously the schedule overlay used an inline switch; this extracted key map provides the
/// same per-view isolation used by every other browser and allows a table-driven consistency test.
/// </summary>
internal static class ScheduleBrowserKeyMap
{
    /// <summary>Resolves <paramref name="key"/> to a command in the context of the list view.</summary>
    public static ScheduleBrowserCommand Map(Key? key)
    {
        if (key is null)
        {
            return ScheduleBrowserCommand.None;
        }

        if (key == Key.Esc || key == Key.Q) return ScheduleBrowserCommand.Close;
        if (key == Key.CursorUp || key == Key.K) return ScheduleBrowserCommand.MoveUp;
        if (key == Key.CursorDown || key == Key.J) return ScheduleBrowserCommand.MoveDown;
        if (key == Key.PageUp) return ScheduleBrowserCommand.PageUp;
        if (key == Key.PageDown) return ScheduleBrowserCommand.PageDown;
        if (key == Key.Home) return ScheduleBrowserCommand.MoveToStart;
        if (key == Key.End) return ScheduleBrowserCommand.MoveToEnd;
        if (key == new Key('d')) return ScheduleBrowserCommand.DeleteSelected;
        if (key == new Key('n')) return ScheduleBrowserCommand.CreateNew;
        if (key == new Key('r')) return ScheduleBrowserCommand.Reload;
        if (key == new Key('/')) return ScheduleBrowserCommand.Filter;
        return ScheduleBrowserCommand.None;
    }
}
