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
        if (key == Key.Esc) return McpBrowserCommand.Close;
        if (key == Key.CursorUp) return McpBrowserCommand.MoveUp;
        if (key == Key.CursorDown) return McpBrowserCommand.MoveDown;
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
        return McpBrowserCommand.None;
    }

    private static McpBrowserCommand MapDetail(Key key)
    {
        if (key == Key.Esc) return McpBrowserCommand.ReturnToList;
        if (key == new Key('e')) return McpBrowserCommand.BeginEdit;
        if (key == Key.Space) return McpBrowserCommand.ToggleEnabled;
        if (key == new Key('u')) return McpBrowserCommand.Reauthenticate;
        if (key == Key.Delete) return McpBrowserCommand.DeleteServer;
        return McpBrowserCommand.None;
    }

    private static McpBrowserCommand MapEditor(Key key)
    {
        if (key == Key.Esc) return McpBrowserCommand.EditorCancel;
        if (key == Key.Tab.WithShift) return McpBrowserCommand.EditorPrevious;
        if (key == Key.Tab) return McpBrowserCommand.EditorNext;
        if (key == Key.Enter) return McpBrowserCommand.EditorApply;
        if (key == Key.Backspace) return McpBrowserCommand.EditorBackspace;
        if (key == Key.Delete) return McpBrowserCommand.EditorDelete;
        if (key == Key.N.WithCtrl) return McpBrowserCommand.EditorAddItem;
        if (key == Key.R.WithCtrl) return McpBrowserCommand.EditorRemoveItem;
        if (key == Key.CursorUp.WithCtrl) return McpBrowserCommand.EditorPreviousItem;
        if (key == Key.CursorDown.WithCtrl) return McpBrowserCommand.EditorNextItem;
        if (key == Key.CursorLeft.WithCtrl) return McpBrowserCommand.EditorPreviousItemPart;
        if (key == Key.CursorRight.WithCtrl) return McpBrowserCommand.EditorNextItemPart;

        var rune = key.AsRune;
        return !key.IsCtrl &&
            !key.IsAlt &&
            rune.Value != 0 &&
            !System.Text.Rune.IsControl(rune)
                ? McpBrowserCommand.EditorInsert
                : McpBrowserCommand.None;
    }
}
