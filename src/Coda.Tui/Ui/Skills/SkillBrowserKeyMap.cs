namespace Coda.Tui.Ui.Skills;

/// <summary>Maps a raw Terminal.Gui key to a <see cref="SkillBrowserCommand"/> for the active pane.</summary>
internal static class SkillBrowserKeyMap
{
    /// <summary>Resolves <paramref name="key"/> to a command in the context of <paramref name="view"/>.</summary>
    public static SkillBrowserCommand Map(Key? key, SkillBrowserView view)
    {
        if (key is null)
        {
            return SkillBrowserCommand.None;
        }

        return view == SkillBrowserView.Detail ? MapDetail(key) : MapList(key);
    }

    private static SkillBrowserCommand MapList(Key key)
    {
        if (key == Key.Esc || key == Key.Q) return SkillBrowserCommand.Close;
        if (key == Key.CursorUp || key == Key.K) return SkillBrowserCommand.MoveUp;
        if (key == Key.CursorDown || key == Key.J) return SkillBrowserCommand.MoveDown;
        if (key == Key.PageUp) return SkillBrowserCommand.PageUp;
        if (key == Key.PageDown) return SkillBrowserCommand.PageDown;
        if (key == Key.Home) return SkillBrowserCommand.MoveToStart;
        if (key == Key.End) return SkillBrowserCommand.MoveToEnd;
        if (key == Key.Enter) return SkillBrowserCommand.OpenDetail;
        if (key == Key.Space) return SkillBrowserCommand.ToggleEnabled;
        if (key == new Key('r')) return SkillBrowserCommand.Reload;
        return SkillBrowserCommand.None;
    }

    private static SkillBrowserCommand MapDetail(Key key)
    {
        if (key == Key.Esc) return SkillBrowserCommand.ReturnToList;
        if (key == Key.Space) return SkillBrowserCommand.ToggleEnabled;
        if (key == new Key('r')) return SkillBrowserCommand.Reload;
        return SkillBrowserCommand.None;
    }
}
