namespace Coda.Tui.Ui.Input;

/// <summary>
/// Minimal, immutable context that <see cref="UiActionMap"/> needs to disambiguate
/// keys whose meaning depends on composer/overlay state. Up/Down resolve to completion
/// selection while a completion popup is open, to visual caret movement while the caret
/// can still move within the wrapped draft, and to history navigation at the draft's
/// visual boundaries.
/// </summary>
public readonly record struct UiInputContext(
    bool ComposerEmpty,
    bool CompletionVisible,
    bool CanMoveVisualUp,
    bool CanMoveVisualDown);

/// <summary>
/// Translates Terminal.Gui <see cref="Key"/> presses into context-independent
/// <see cref="UiAction"/> values. Ordinary printable/unmapped keys return
/// <see cref="UiAction.None"/> so the composer can insert them as text.
/// </summary>
public static class UiActionMap
{
    public static UiAction Map(Key key, UiInputContext context)
    {
        if (key is null)
        {
            return UiAction.None;
        }

        // Ctrl+J is checked before any plain-letter handling so it always wins over an
        // ordinary "j" keystroke.
        if (key == Key.J.WithCtrl)
        {
            return UiAction.InsertNewline;
        }

        if (key == Key.Enter)
        {
            return UiAction.Submit;
        }

        if (key == Key.L.WithCtrl)
        {
            return UiAction.ForceRedraw;
        }

        // Ctrl+Up/Down always navigate submission history, regardless of the caret's visual row,
        // so a multiline draft never traps history navigation on an interior row.
        if (key == Key.CursorUp.WithCtrl)
        {
            return UiAction.HistoryPrevious;
        }

        if (key == Key.CursorDown.WithCtrl)
        {
            return UiAction.HistoryNext;
        }

        // Plain Up/Down: completion selection wins while a popup is open, then visual caret
        // movement while the caret can still move within the wrapped draft, then history at the
        // visual boundary.
        if (key == Key.CursorUp)
        {
            return context.CompletionVisible
                ? UiAction.CompletionPrevious
                : context.CanMoveVisualUp
                    ? UiAction.CursorVisualUp
                    : UiAction.HistoryPrevious;
        }

        if (key == Key.CursorDown)
        {
            return context.CompletionVisible
                ? UiAction.CompletionNext
                : context.CanMoveVisualDown
                    ? UiAction.CursorVisualDown
                    : UiAction.HistoryNext;
        }

        // Ctrl+Arrow performs word movement; the plain arrows move by one grapheme. These
        // are routed through the controller (the caret source of truth) so a subsequent
        // insert/paste lands at the moved position instead of a stale one.
        if (key == Key.CursorLeft.WithCtrl)
        {
            return UiAction.WordLeft;
        }

        if (key == Key.CursorRight.WithCtrl)
        {
            return UiAction.WordRight;
        }

        if (key == Key.CursorLeft)
        {
            return UiAction.CursorLeft;
        }

        if (key == Key.CursorRight)
        {
            return UiAction.CursorRight;
        }

        if (key == Key.Home)
        {
            return UiAction.LineStart;
        }

        if (key == Key.End)
        {
            return UiAction.LineEnd;
        }

        if (key == Key.Tab)
        {
            return UiAction.CompleteSuggestion;
        }

        if (key == Key.Esc)
        {
            return context.CompletionVisible ? UiAction.DismissCompletion : UiAction.None;
        }

        if (key == Key.PageUp)
        {
            return UiAction.TranscriptUp;
        }

        if (key == Key.PageDown)
        {
            return UiAction.TranscriptDown;
        }

        if (key == Key.F2)
        {
            return UiAction.ToggleMode;
        }

        return UiAction.None;
    }
}
