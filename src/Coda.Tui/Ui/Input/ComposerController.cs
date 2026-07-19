using System.Collections.Immutable;
using System.Globalization;
using Coda.Tui.Repl;

namespace Coda.Tui.Ui.Input;

/// <summary>
/// Terminal.Gui-independent composer behavior: draft editing, caret movement, slash
/// command completion, submission history, and paste tracking. All caret positions are
/// .NET (UTF-16) string indices, kept clamped to <c>[0, Draft.Length]</c>. The
/// controller is the single source of truth for composer content; views mirror it.
/// </summary>
internal sealed class ComposerController
{
    private readonly SlashCommandCompletion completion;
    private string historyStash = string.Empty;
    private int historyStashCursor;

    public ComposerController(SlashCommandCompletion completion)
    {
        this.completion = completion ?? throw new ArgumentNullException(nameof(completion));
        this.State = ComposerState.Empty;
        this.RefreshCompletion();
    }

    public ComposerState State { get; private set; }

    /// <summary>The slash command suggestions currently offered, or empty when none/dismissed.</summary>
    public IReadOnlyList<ISlashCommand> Suggestions =>
        this.completion.IsVisible ? this.completion.Suggestions : [];

    /// <summary>The selected suggestion index while suggestions are visible, or -1 when none/dismissed.</summary>
    public int SelectedSuggestionIndex =>
        this.completion.IsVisible ? this.completion.SelectedIndex : -1;

    public void ReplaceDraft(string text, int cursorIndex)
    {
        text ??= string.Empty;
        var cursor = Math.Clamp(cursorIndex, 0, text.Length);
        this.State = this.State with { Draft = text, CursorIndex = cursor, PreferredDisplayColumn = null };
        this.RefreshCompletion();
    }

    /// <summary>
    /// Mirrors a native editor content change: replaces the draft text while keeping the current caret
    /// (clamped to the new length). The precise caret is set separately from the editor's unwrapped cursor
    /// event, so this never reconstructs the caret from wrapped coordinates.
    /// </summary>
    public void SetDraftText(string text)
    {
        text ??= string.Empty;
        var cursor = Math.Clamp(this.State.CursorIndex, 0, text.Length);
        this.State = this.State with { Draft = text, CursorIndex = cursor, PreferredDisplayColumn = null };
        this.RefreshCompletion();
    }

    public void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var draft = this.State.Draft;
        var cursor = Math.Clamp(this.State.CursorIndex, 0, draft.Length);
        var updated = draft.Insert(cursor, text);
        this.State = this.State with
        {
            Draft = updated,
            CursorIndex = cursor + text.Length,
            PreferredDisplayColumn = null,
        };
        this.RefreshCompletion();
    }

    public void SeedHistory(IEnumerable<string> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var items = history.Where(item => item is not null).ToImmutableArray();
        this.State = this.State with { History = items, HistoryIndex = items.Length };
    }

    public void BeginPaste() => this.State = this.State with { PasteActive = true };

    public void EndPaste() => this.State = this.State with { PasteActive = false };

    /// <summary>Records the top visual row currently scrolled into view, clamped to non-negative.</summary>
    public void UpdateViewport(int scrollRow) =>
        this.State = this.State with { ScrollRow = Math.Max(0, scrollRow) };

    /// <summary>
    /// Moves the caret to an explicit UTF-16 index and sets the preferred display column carried across
    /// vertical movement. Passing <c>null</c> clears the preferred column so the next vertical move re-seeds
    /// it from the caret's current column.
    /// </summary>
    public void MoveCursorTo(int cursorIndex, int? preferredDisplayColumn = null)
    {
        var clamped = Math.Clamp(cursorIndex, 0, this.State.Draft.Length);
        this.State = this.State with
        {
            CursorIndex = clamped,
            PreferredDisplayColumn = preferredDisplayColumn,
        };
        this.RefreshCompletion();
    }

    /// <summary>Clears the preferred display column so the next vertical move re-seeds it from the caret.</summary>
    public void ResetPreferredDisplayColumn() =>
        this.State = this.State with { PreferredDisplayColumn = null };

    public ComposerActionResult Apply(UiAction action)
    {
        switch (action)
        {
            case UiAction.Submit:
                return this.Submit();
            case UiAction.InsertNewline:
                this.InsertText("\n");
                return Redraw();
            case UiAction.CompleteSuggestion:
                this.CompleteSuggestion();
                return Redraw();
            case UiAction.CompletionPrevious:
                this.completion.MoveSelection(-1);
                return Redraw();
            case UiAction.CompletionNext:
                this.completion.MoveSelection(1);
                return Redraw();
            case UiAction.DismissCompletion:
                this.completion.Dismiss();
                return Redraw();
            case UiAction.HistoryPrevious:
                this.HistoryPrevious();
                return Redraw();
            case UiAction.HistoryNext:
                this.HistoryNext();
                return Redraw();
            case UiAction.CursorLeft:
                this.MoveCursor(this.PreviousElement(this.State.CursorIndex));
                return Redraw();
            case UiAction.CursorRight:
                this.MoveCursor(this.NextElement(this.State.CursorIndex));
                return Redraw();
            case UiAction.WordLeft:
                this.MoveCursor(this.PreviousWord(this.State.CursorIndex));
                return Redraw();
            case UiAction.WordRight:
                this.MoveCursor(this.NextWord(this.State.CursorIndex));
                return Redraw();
            case UiAction.LineStart:
                this.MoveCursor(this.CurrentLineStart(this.State.CursorIndex));
                return Redraw();
            case UiAction.LineEnd:
                this.MoveCursor(this.CurrentLineEnd(this.State.CursorIndex));
                return Redraw();
            default:
                return new ComposerActionResult(null, false);
        }
    }

    public ComposerState Export() => this.State;

    public void Restore(ComposerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var draft = state.Draft ?? string.Empty;
        var cursor = Math.Clamp(state.CursorIndex, 0, draft.Length);
        var history = state.History.IsDefault ? ImmutableArray<string>.Empty : state.History;
        var historyIndex = Math.Clamp(state.HistoryIndex, 0, history.Length);
        var scrollRow = Math.Max(0, state.ScrollRow);
        var preferred = state.PreferredDisplayColumn is { } column && column >= 0 ? column : (int?)null;
        this.State = new ComposerState(
            draft, cursor, history, historyIndex, state.PasteActive, scrollRow, preferred);
        this.historyStash = draft;
        this.historyStashCursor = cursor;
        this.RefreshCompletion();
    }

    private static ComposerActionResult Redraw() => new(null, true);

    private ComposerActionResult Submit()
    {
        if (this.State.PasteActive)
        {
            return new ComposerActionResult(null, false);
        }

        var draft = this.State.Draft;
        if (string.IsNullOrWhiteSpace(draft))
        {
            return new ComposerActionResult(null, false);
        }

        var history = this.State.History.IsDefault ? ImmutableArray<string>.Empty : this.State.History;
        if (history.IsEmpty || !string.Equals(history[^1], draft, StringComparison.Ordinal))
        {
            history = history.Add(draft);
        }

        this.historyStash = string.Empty;
        this.historyStashCursor = 0;
        this.State = ComposerState.Empty with { History = history, HistoryIndex = history.Length };
        this.RefreshCompletion();
        return new ComposerActionResult(draft, true);
    }

    private void CompleteSuggestion()
    {
        if (this.completion.Complete() is not { } completed)
        {
            return;
        }

        var draft = this.State.Draft;
        var cursor = Math.Clamp(this.State.CursorIndex, 0, draft.Length);
        var newDraft = completed + draft[cursor..];
        this.State = this.State with
        {
            Draft = newDraft,
            CursorIndex = completed.Length,
            PreferredDisplayColumn = null,
        };
        this.RefreshCompletion();
    }

    private void HistoryPrevious()
    {
        var history = this.State.History;
        if (history.IsDefaultOrEmpty)
        {
            return;
        }

        if (this.State.HistoryIndex >= history.Length)
        {
            // Starting navigation from the live draft: remember it so HistoryNext can restore it.
            this.historyStash = this.State.Draft;
            this.historyStashCursor = this.State.CursorIndex;
        }

        var current = Math.Min(this.State.HistoryIndex, history.Length);
        var newIndex = Math.Max(0, current - 1);
        var entry = history[newIndex];
        this.State = this.State with
        {
            Draft = entry,
            CursorIndex = entry.Length,
            HistoryIndex = newIndex,
            PreferredDisplayColumn = null,
        };
        this.RefreshCompletion();
    }

    private void HistoryNext()
    {
        var history = this.State.History;
        if (history.IsDefaultOrEmpty || this.State.HistoryIndex >= history.Length)
        {
            return;
        }

        var newIndex = this.State.HistoryIndex + 1;
        if (newIndex >= history.Length)
        {
            var draft = this.historyStash;
            var cursor = Math.Clamp(this.historyStashCursor, 0, draft.Length);
            this.State = this.State with
            {
                Draft = draft,
                CursorIndex = cursor,
                HistoryIndex = history.Length,
                PreferredDisplayColumn = null,
            };
            this.RefreshCompletion();
            return;
        }

        var entry = history[newIndex];
        this.State = this.State with
        {
            Draft = entry,
            CursorIndex = entry.Length,
            HistoryIndex = newIndex,
            PreferredDisplayColumn = null,
        };
        this.RefreshCompletion();
    }

    private void MoveCursor(int cursorIndex)
    {
        var clamped = Math.Clamp(cursorIndex, 0, this.State.Draft.Length);
        this.State = this.State with { CursorIndex = clamped, PreferredDisplayColumn = null };
        this.RefreshCompletion();
    }

    private void RefreshCompletion()
    {
        this.completion.Reactivate();
        this.completion.Update(this.State.Draft, this.State.CursorIndex);
    }

    private int PreviousElement(int index)
    {
        var draft = this.State.Draft;
        if (index <= 0)
        {
            return 0;
        }

        var previous = 0;
        foreach (var start in TextElementStarts(draft))
        {
            if (start >= index)
            {
                break;
            }

            previous = start;
        }

        return previous;
    }

    private int NextElement(int index)
    {
        var draft = this.State.Draft;
        foreach (var start in TextElementStarts(draft))
        {
            if (start > index)
            {
                return start;
            }
        }

        return draft.Length;
    }

    private int PreviousWord(int index)
    {
        var draft = this.State.Draft;
        var i = Math.Clamp(index, 0, draft.Length);
        while (i > 0 && char.IsWhiteSpace(draft[i - 1]))
        {
            i--;
        }

        while (i > 0 && !char.IsWhiteSpace(draft[i - 1]))
        {
            i--;
        }

        return i;
    }

    private int NextWord(int index)
    {
        var draft = this.State.Draft;
        var i = Math.Clamp(index, 0, draft.Length);
        while (i < draft.Length && !char.IsWhiteSpace(draft[i]))
        {
            i++;
        }

        while (i < draft.Length && char.IsWhiteSpace(draft[i]))
        {
            i++;
        }

        return i;
    }

    private int CurrentLineStart(int index)
    {
        var draft = this.State.Draft;
        if (draft.Length == 0)
        {
            return 0;
        }

        var i = Math.Clamp(index, 0, draft.Length);
        if (i == 0)
        {
            return 0;
        }

        var newline = draft.LastIndexOf('\n', i - 1);
        return newline < 0 ? 0 : newline + 1;
    }

    private int CurrentLineEnd(int index)
    {
        var draft = this.State.Draft;
        var i = Math.Clamp(index, 0, draft.Length);
        var newline = draft.IndexOf('\n', i);
        return newline < 0 ? draft.Length : newline;
    }

    private static IEnumerable<int> TextElementStarts(string value)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            yield return enumerator.ElementIndex;
        }
    }
}
