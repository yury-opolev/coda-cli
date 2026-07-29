namespace Coda.Tui.Ui.Plugins;

/// <summary>Maps a raw Terminal.Gui key to a <see cref="PluginBrowserCommand"/> for the active pane.</summary>
internal static class PluginBrowserKeyMap
{
    /// <summary>Resolves <paramref name="key"/> to a command in the context of <paramref name="view"/>.</summary>
    public static PluginBrowserCommand Map(Key? key, PluginBrowserView view)
    {
        if (key is null)
        {
            return PluginBrowserCommand.None;
        }

        return view == PluginBrowserView.Detail ? MapDetail(key) : MapList(key);
    }

    private static PluginBrowserCommand MapList(Key key)
    {
        if (key == Key.Esc || key == Key.Q) return PluginBrowserCommand.Close;
        if (key == Key.CursorUp || key == Key.K) return PluginBrowserCommand.MoveUp;
        if (key == Key.CursorDown || key == Key.J) return PluginBrowserCommand.MoveDown;
        if (key == Key.PageUp) return PluginBrowserCommand.PageUp;
        if (key == Key.PageDown) return PluginBrowserCommand.PageDown;
        if (key == Key.Home) return PluginBrowserCommand.MoveToStart;
        if (key == Key.End) return PluginBrowserCommand.MoveToEnd;
        if (key == Key.Enter) return PluginBrowserCommand.OpenDetail;
        if (key == Key.Space) return PluginBrowserCommand.ToggleEnabled;
        if (key == new Key('u')) return PluginBrowserCommand.Update;
        return PluginBrowserCommand.None;
    }

    private static PluginBrowserCommand MapDetail(Key key)
    {
        if (key == Key.Esc) return PluginBrowserCommand.ReturnToList;
        if (key == Key.Space) return PluginBrowserCommand.ToggleEnabled;
        if (key == new Key('u')) return PluginBrowserCommand.Update;
        return PluginBrowserCommand.None;
    }
}
