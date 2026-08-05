namespace Coda.Tui.Ui.Mcp;

internal static class McpBrowserKeyMap
{
    public static McpBrowserCommand Map(Key? key, McpBrowserView view)
    {
        if (key is null)
        {
            return McpBrowserCommand.None;
        }

        return view switch
        {
            McpBrowserView.Editor => MapEditor(key),
            McpBrowserView.Detail => MapDetail(key),
            _ => MapList(key),
        };
    }

    private static McpBrowserCommand MapList(Key key)
    {
        if (key == Key.Esc || key == Key.Q) return McpBrowserCommand.Close;
        if (key == Key.CursorUp || key == Key.K) return McpBrowserCommand.MoveUp;
        if (key == Key.CursorDown || key == Key.J) return McpBrowserCommand.MoveDown;
        if (key == Key.PageUp) return McpBrowserCommand.PageUp;
        if (key == Key.PageDown) return McpBrowserCommand.PageDown;
        if (key == Key.Home) return McpBrowserCommand.MoveToStart;
        if (key == Key.End) return McpBrowserCommand.MoveToEnd;
        if (key == Key.Enter) return McpBrowserCommand.OpenDetail;
        if (key == new Key('a')) return McpBrowserCommand.BeginAdd;
        if (key == new Key('e')) return McpBrowserCommand.BeginEdit;
        if (key == Key.Space) return McpBrowserCommand.ToggleEnabled;
        if (key == new Key('u')) return McpBrowserCommand.Reauthenticate;
        if (key == Key.Delete) return McpBrowserCommand.DeleteServer;
        if (key == new Key('r')) return McpBrowserCommand.Reload;
        if (key == new Key('/')) return McpBrowserCommand.Filter;
        return McpBrowserCommand.None;
    }

    private static McpBrowserCommand MapDetail(Key key)
    {
        if (key == Key.Esc || key == Key.Q) return McpBrowserCommand.ReturnToList;
        // Up/Down and k/j scroll the detail pane (handled by TryScrollDetail in the overlay)
        // rather than changing the underlying list selection. Returning None lets the overlay's
        // TryScrollDetail path claim those keys before falling through to no-op.
        if (key == new Key('e')) return McpBrowserCommand.BeginEdit;
        if (key == Key.Space) return McpBrowserCommand.ToggleEnabled;
        if (key == new Key('u')) return McpBrowserCommand.Reauthenticate;
        if (key == Key.Delete) return McpBrowserCommand.DeleteServer;
        return McpBrowserCommand.None;
    }

    private static McpBrowserCommand MapEditor(Key key)
    {
        if (key == Key.Esc) return McpBrowserCommand.EditorCancel;
        if (key == Key.Enter) return McpBrowserCommand.EditorApply;
        if (key == Key.N.WithCtrl) return McpBrowserCommand.EditorAddItem;
        if (key == Key.R.WithCtrl) return McpBrowserCommand.EditorRemoveItem;

        // Item reordering is Alt+Up / Alt+Down. Ctrl+arrows are intentionally NOT mapped anymore:
        // with per-item widgets (Task 8) plain Tab/Shift+Tab and Up/Down navigate between fields and
        // items, so the old Ctrl-based item navigation would only shadow those widget defaults.
        if (key == Key.CursorUp.WithAlt) return McpBrowserCommand.EditorReorderUp;
        if (key == Key.CursorDown.WithAlt) return McpBrowserCommand.EditorReorderDown;
        return McpBrowserCommand.None;
    }
}
