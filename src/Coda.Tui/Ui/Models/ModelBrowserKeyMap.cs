namespace Coda.Tui.Ui.Models;

/// <summary>Maps a raw Terminal.Gui key to a <see cref="ModelBrowserCommand"/>.</summary>
internal static class ModelBrowserKeyMap
{
    /// <summary>Resolves <paramref name="key"/> to a model browser command.</summary>
    public static ModelBrowserCommand Map(Key? key)
    {
        if (key is null)
        {
            return ModelBrowserCommand.None;
        }

        if (key == Key.Esc || key == Key.Q) return ModelBrowserCommand.Close;
        if (key == Key.CursorUp || key == Key.K) return ModelBrowserCommand.MoveUp;
        if (key == Key.CursorDown || key == Key.J) return ModelBrowserCommand.MoveDown;
        if (key == Key.PageUp) return ModelBrowserCommand.PageUp;
        if (key == Key.PageDown) return ModelBrowserCommand.PageDown;
        if (key == Key.Home) return ModelBrowserCommand.MoveToStart;
        if (key == Key.End) return ModelBrowserCommand.MoveToEnd;
        if (key == Key.Enter) return ModelBrowserCommand.Select;
        if (key == new Key('r')) return ModelBrowserCommand.Reload;
        if (key == new Key('/')) return ModelBrowserCommand.Filter;
        return ModelBrowserCommand.None;
    }
}
