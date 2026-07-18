namespace Coda.Tui.Ui.Input;

/// <summary>
/// Minimal, immutable context that <see cref="UiActionMap"/> needs to disambiguate
/// keys whose meaning depends on composer/overlay state (for example, arrow keys drive
/// completion selection while a completion popup is open and history otherwise).
/// </summary>
public readonly record struct UiInputContext(bool ComposerEmpty, bool CompletionVisible);

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

        if (key == Key.C.WithCtrl)
        {
            return UiAction.Interrupt;
        }

        if (key == Key.D.WithCtrl)
        {
            return context.ComposerEmpty ? UiAction.Exit : UiAction.None;
        }

        if (key == Key.L.WithCtrl)
        {
            return UiAction.ForceRedraw;
        }

        if (key == Key.CursorUp)
        {
            return context.CompletionVisible ? UiAction.CompletionPrevious : UiAction.HistoryPrevious;
        }

        if (key == Key.CursorDown)
        {
            return context.CompletionVisible ? UiAction.CompletionNext : UiAction.HistoryNext;
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
            return UiAction.DismissCompletion;
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
