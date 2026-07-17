// The whole file opts out of CS0618 because Terminal.Gui 2.4.17 marks TextView as
// obsolete (superseded by an external Editor package), yet it remains the supported
// multiline editor in the released package. The suppression is intentionally scoped to
// this single file so no other production code silences the warning.
#pragma warning disable CS0618

using System.Text;
using Coda.Tui.Repl;

namespace Coda.Tui.Ui.Input;

/// <summary>
/// Multiline Terminal.Gui composer built on <see cref="TextView"/>. Key handling is
/// routed through <see cref="UiActionMap"/> and a Terminal.Gui-independent
/// <see cref="ComposerController"/>, which owns the authoritative draft/caret/history
/// state; the <see cref="TextView"/> is kept as a synchronized mirror. Enter submits,
/// Ctrl+J inserts a newline, and bracketed paste is inserted literally so embedded
/// newlines can never trigger a submission.
/// </summary>
internal sealed class ComposerView : TextView
{
    private readonly ComposerController controller;
    private bool syncingText;

    public ComposerView(ComposerController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.Multiline = true;
        this.WordWrap = true;
        this.TabKeyAddsTab = false;
        this.ContentsChanged += this.OnContentsChangedSync;
        this.SyncTextView();
    }

    /// <summary>Raised when the user submits the composer; carries the submitted text.</summary>
    public event EventHandler<string>? Submitted;

    /// <summary>Raised for shell-level actions (interrupt, exit, toggle mode, transcript scrolling, ...).</summary>
    public event EventHandler<UiAction>? ActionRequested;

    /// <summary>Current slash command suggestions, exposed so the shell can render an overlay.</summary>
    public IReadOnlyList<ISlashCommand> Suggestions => this.controller.Suggestions;

    /// <summary>
    /// Number of content lines in the draft. It is independent of suggestion visibility,
    /// so showing/hiding the suggestion overlay never changes the composer's persistent height.
    /// </summary>
    public int DraftLineCount => this.controller.State.Draft.Count(character => character == '\n') + 1;

    public void SetDraft(string text, int cursorIndex)
    {
        this.controller.ReplaceDraft(text ?? string.Empty, cursorIndex);
        this.SyncTextView();
    }

    public string GetDraft() => this.controller.State.Draft;

    public ComposerState GetState() => this.controller.State;

    /// <summary>
    /// Injects a paste payload the same way the driver would deliver a bracketed paste.
    /// Public so tests can exercise paste handling without a live console driver.
    /// </summary>
    public bool NewPasteEvent(string text) => this.OnPaste(text ?? string.Empty);

    protected override bool OnKeyDown(Key key)
    {
        var context = new UiInputContext(
            ComposerEmpty: this.controller.State.Draft.Length == 0,
            CompletionVisible: this.controller.Suggestions.Count > 0);
        var action = UiActionMap.Map(key, context);

        // While a paste is in progress, a stray Enter is text, never a submission.
        if (action == UiAction.Submit && this.controller.State.PasteActive)
        {
            action = UiAction.InsertNewline;
        }

        if (action != UiAction.None)
        {
            return this.HandleAction(action);
        }

        if (TryGetPrintableText(key, out var text))
        {
            this.SyncCursorFromView();
            this.controller.InsertText(text);
            this.SyncTextView();
            return true;
        }

        // Ordinary editing keys (backspace, delete, arrows, home/end) are handled by the
        // base TextView, then mirrored back into the controller.
        var handled = base.OnKeyDown(key);
        this.SyncControllerFromTextView();
        return handled;
    }

    protected override bool OnPaste(string text)
    {
        this.SyncCursorFromView();
        this.controller.BeginPaste();
        try
        {
            this.controller.InsertText(NormalizeNewlines(text ?? string.Empty));
        }
        finally
        {
            this.controller.EndPaste();
        }

        this.SyncTextView();
        return true;
    }

    private bool HandleAction(UiAction action)
    {
        switch (action)
        {
            case UiAction.Submit:
                var result = this.controller.Apply(UiAction.Submit);
                this.SyncTextView();
                if (result.SubmittedText is { } submitted)
                {
                    this.Submitted?.Invoke(this, submitted);
                }

                return true;

            case UiAction.InsertNewline:
            case UiAction.CompleteSuggestion:
            case UiAction.CompletionPrevious:
            case UiAction.CompletionNext:
            case UiAction.DismissCompletion:
            case UiAction.HistoryPrevious:
            case UiAction.HistoryNext:
            case UiAction.CursorLeft:
            case UiAction.CursorRight:
            case UiAction.WordLeft:
            case UiAction.WordRight:
            case UiAction.LineStart:
            case UiAction.LineEnd:
                this.controller.Apply(action);
                this.SyncTextView();
                return true;

            default:
                // Interrupt, Exit, ToggleMode, ForceRedraw, Open*, Transcript*, JumpToNewest.
                this.ActionRequested?.Invoke(this, action);
                return true;
        }
    }

    private void OnContentsChangedSync(object? sender, ContentsChangedEventArgs e) =>
        this.SyncControllerFromTextView();

    /// <summary>
    /// Defense in depth for caret paths the controller did not directly drive (for
    /// example a future mouse click): reconcile the controller from the visible caret
    /// before inserting. It is a no-op until the view is laid out, because an unlaid-out
    /// <see cref="TextView"/> reports its insertion point as the origin regardless of the
    /// controller's authoritative caret.
    /// </summary>
    private void SyncCursorFromView()
    {
        if (this.syncingText || !this.IsInitialized)
        {
            return;
        }

        this.SyncControllerFromTextView();
    }

    private void SyncTextView()
    {
        var state = this.controller.State;
        var text = state.Draft.Replace("\n", Environment.NewLine);
        this.syncingText = true;
        try
        {
            if (!string.Equals(this.Text, text, StringComparison.Ordinal))
            {
                this.Text = text;
            }

            this.ApplyCursor(state.Draft, state.CursorIndex);
        }
        finally
        {
            this.syncingText = false;
        }
    }

    private void SyncControllerFromTextView()
    {
        if (this.syncingText)
        {
            return;
        }

        var draft = NormalizeNewlines(this.Text ?? string.Empty);
        var cursor = FlatCursorIndex(draft, this.InsertionPoint);
        this.controller.ReplaceDraft(draft, cursor);
    }

    private void ApplyCursor(string draft, int cursorIndex)
    {
        var clamped = Math.Clamp(cursorIndex, 0, draft.Length);
        var row = 0;
        var lineStart = 0;
        for (var i = 0; i < clamped; i++)
        {
            if (draft[i] == '\n')
            {
                row++;
                lineStart = i + 1;
            }
        }

        var column = CountRunes(draft.AsSpan(lineStart, clamped - lineStart));
        this.InsertionPoint = new System.Drawing.Point(column, row);
    }

    private static int FlatCursorIndex(string draft, System.Drawing.Point point)
    {
        var lines = draft.Split('\n');
        var row = Math.Clamp(point.Y, 0, lines.Length - 1);
        var index = 0;
        for (var i = 0; i < row; i++)
        {
            index += lines[i].Length + 1;
        }

        index += RuneColumnToUtf16Offset(lines[row], point.X);
        return Math.Clamp(index, 0, draft.Length);
    }

    private static int RuneColumnToUtf16Offset(string line, int runeColumn)
    {
        if (runeColumn <= 0)
        {
            return 0;
        }

        var offset = 0;
        var count = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (count >= runeColumn)
            {
                break;
            }

            offset += rune.Utf16SequenceLength;
            count++;
        }

        return Math.Min(offset, line.Length);
    }

    private static int CountRunes(ReadOnlySpan<char> value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    private static bool TryGetPrintableText(Key key, out string text)
    {
        text = string.Empty;
        if (key is null || key.IsCtrl || key.IsAlt)
        {
            return false;
        }

        var rune = key.AsRune;
        if (rune.Value == 0 || Rune.IsControl(rune))
        {
            return false;
        }

        text = rune.ToString();
        return true;
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n").Replace("\r", "\n");
}

#pragma warning restore CS0618
