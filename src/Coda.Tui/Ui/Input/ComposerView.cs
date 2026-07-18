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
    private bool inputEnabled = true;
    private IReadOnlyList<ISlashCommand> lastSuggestions = [];
    private int lastSelectedIndex = -1;

    public ComposerView(ComposerController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.Multiline = true;
        this.WordWrap = true;
        this.TabKeyAddsTab = false;

        // Slash-command completion is rendered by the shell-owned CommandCompletionView, so keep the base
        // TextView's built-in word autocomplete inert: an empty suggestion generator ensures it never
        // surfaces suggestions of its own. Terminal.Gui still parents the (empty) autocomplete popup to the
        // running top-level on the first edit; the shell strips that stray sub-view so the fixed insertion
        // order — and the modal prompt overlay's topmost z-order — is preserved.
        this.Autocomplete.SuggestionGenerator = new NoSuggestionGenerator();

        this.ContentsChanged += this.OnContentsChangedSync;
        this.SyncTextView();
        this.SnapshotCompletion();
    }

    /// <summary>Raised when the user submits the composer; carries the submitted text.</summary>
    public event EventHandler<string>? Submitted;

    /// <summary>Raised for shell-level actions (interrupt, exit, toggle mode, transcript scrolling, ...).</summary>
    public event EventHandler<UiAction>? ActionRequested;

    /// <summary>
    /// Raised whenever the composer's measured content or caret may have changed — a draft replacement,
    /// printable edit, base editor edit, paste, completion, history navigation, or submission — so the shell
    /// can remeasure the composer's height and re-apply its internal scroll to keep the caret visible.
    /// </summary>
    public event EventHandler? LayoutInvalidated;

    /// <summary>
    /// The seam that produces the grapheme-aware visual layout used to size and internally scroll the
    /// composer. Tests substitute a throwing factory to simulate a failed measurement; production uses the
    /// real <see cref="ComposerVisualLayout.Create"/>.
    /// </summary>
    internal Func<string, int, ComposerVisualLayout> LayoutFactory { get; set; } =
        ComposerVisualLayout.Create;

    /// <summary>
    /// Raised only when the slash-command completion actually changes — its offered suggestions, the
    /// selected index, or its visibility — so a host can (re)render the completion menu without redraw
    /// loops. Never fires while the completion is unchanged (e.g. plain typing with no suggestions).
    /// </summary>
    public event EventHandler? CompletionChanged;

    /// <summary>Current slash command suggestions, exposed so the shell can render an overlay.</summary>
    public IReadOnlyList<ISlashCommand> Suggestions => this.controller.Suggestions;

    /// <summary>The selected suggestion index while suggestions are visible, or -1 when none.</summary>
    public int SelectedSuggestionIndex => this.controller.SelectedSuggestionIndex;

    /// <summary>
    /// Number of content lines in the draft. It is independent of suggestion visibility,
    /// so showing/hiding the suggestion overlay never changes the composer's persistent height.
    /// </summary>
    public int DraftLineCount => this.controller.State.Draft.Count(character => character == '\n') + 1;

    /// <summary>
    /// Whether the composer accepts key input. The shell disables it while the semantic startup operation
    /// is active so a submission (or any edit) can never race initialization, then re-enables it once the
    /// snapshot reports ready. While disabled, key events are swallowed rather than acted on.
    /// </summary>
    internal bool InputEnabled
    {
        get => this.inputEnabled;
        set => this.inputEnabled = value;
    }

    /// <summary>
    /// An optional shell-level key handler consulted before the composer's own key handling (but after the
    /// startup-disabled guard). The shell uses it to claim keys it must own regardless of focus; when it
    /// returns true the composer treats the key as handled and does nothing further.
    /// </summary>
    internal Func<Key, bool>? ShellKeyHandler { get; set; }

    /// <summary>
    /// Inserts <paramref name="text"/> into the draft as if typed, used when the shell redirects printable
    /// input that arrived while another view had focus. No-op while input is disabled or the text is empty.
    /// </summary>
    internal void InsertFromShell(string text)
    {
        if (!this.InputEnabled || string.IsNullOrEmpty(text))
        {
            return;
        }

        this.controller.InsertText(text);
        this.SyncTextView();
        this.RaiseCompletionIfChanged();
        this.LayoutInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public void SetDraft(string text, int cursorIndex)
    {
        this.controller.ReplaceDraft(text ?? string.Empty, cursorIndex);
        this.SyncTextView();
        this.RaiseCompletionIfChanged();
        this.RaiseLayoutInvalidated();
    }

    /// <summary>
    /// Dismisses an open slash-command completion, syncing the mirror and raising a completion change so
    /// the shell can hide the overlay. Returns false (a no-op) when no suggestions are showing, letting the
    /// shell's chord arbitration fall through to the next handler.
    /// </summary>
    internal bool DismissCompletion()
    {
        if (this.controller.Suggestions.Count == 0)
        {
            return false;
        }

        this.controller.Apply(UiAction.DismissCompletion);
        this.SyncTextView();
        this.RaiseCompletionIfChanged();
        return true;
    }

    public string GetDraft() => this.controller.State.Draft;

    public ComposerState GetState() => this.controller.State;

    /// <summary>
    /// The composer's maximum height in rows for a given screen height: never below the three-row minimum
    /// and never above eight, otherwise the floor of 35% of the available screen height.
    /// </summary>
    internal static int MaximumHeight(int screenHeight) =>
        Math.Max(3, Math.Min(8, (int)Math.Floor(Math.Max(0, screenHeight) * 0.35)));

    /// <summary>Measures the current draft's visual layout at <paramref name="width"/> display cells.</summary>
    internal ComposerVisualLayout MeasureLayout(int width) =>
        this.LayoutFactory(this.controller.State.Draft, Math.Max(1, width));

    /// <summary>
    /// The composer's desired height for the given content width and screen height: the wrapped visual line
    /// count clamped to <c>[3, MaximumHeight(screenHeight)]</c>.
    /// </summary>
    internal int DesiredHeight(int width, int screenHeight)
    {
        var visualRows = this.MeasureLayout(width).VisualLineCount;
        return Math.Min(MaximumHeight(screenHeight), Math.Max(3, visualRows));
    }

    /// <summary>
    /// Re-measures the draft, scrolls the internal viewport so the caret's visual row stays within the
    /// visible <paramref name="height"/> rows, records the resulting scroll row on the controller, and
    /// mirrors the caret to its wrapped visual position.
    /// </summary>
    internal void ApplyViewport(int width, int height)
    {
        var layout = this.MeasureLayout(width);
        var caret = layout.PositionForIndex(this.controller.State.CursorIndex);
        var visibleRows = Math.Max(1, height);
        var top = this.controller.State.ScrollRow;
        if (caret.Row < top)
        {
            top = caret.Row;
        }
        else if (caret.Row >= top + visibleRows)
        {
            top = caret.Row - visibleRows + 1;
        }

        top = Math.Clamp(top, 0, Math.Max(0, layout.VisualLineCount - visibleRows));
        this.controller.UpdateViewport(top);
        this.ScrollTo(new System.Drawing.Point(0, top));
        var position = layout.PositionForIndex(this.controller.State.CursorIndex);
        this.InsertionPoint = new System.Drawing.Point(position.Column, position.Row);
    }

    /// <summary>
    /// Injects a paste payload the same way the driver would deliver a bracketed paste.
    /// Public so tests can exercise paste handling without a live console driver.
    /// </summary>
    public bool NewPasteEvent(string text) => this.OnPaste(text ?? string.Empty);

    protected override bool OnKeyDown(Key key)
    {
        // While startup is active the shell disables input; swallow keys so no submission or edit can
        // race initialization, and never surface a completion change from an ignored keystroke.
        if (!this.inputEnabled)
        {
            return true;
        }

        if (this.ShellKeyHandler?.Invoke(key) == true)
        {
            return true;
        }

        var handled = this.HandleKeyDown(key);
        this.RaiseCompletionIfChanged();
        return handled;
    }

    private bool HandleKeyDown(Key key)
    {
        var layout = this.MeasureLayout(Math.Max(1, this.Viewport.Width));
        var caret = layout.PositionForIndex(this.controller.State.CursorIndex);
        var context = new UiInputContext(
            ComposerEmpty: this.controller.State.Draft.Length == 0,
            CompletionVisible: this.controller.Suggestions.Count > 0,
            CanMoveVisualUp: caret.Row > 0,
            CanMoveVisualDown: caret.Row < layout.VisualLineCount - 1);
        var action = UiActionMap.Map(key, context);

        // While a paste is in progress, a stray Enter is text, never a submission.
        if (action == UiAction.Submit && this.controller.State.PasteActive)
        {
            action = UiAction.InsertNewline;
        }

        if (action is UiAction.CursorVisualUp or UiAction.CursorVisualDown)
        {
            return this.MoveVisual(layout, action);
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
            this.RaiseLayoutInvalidated();
            return true;
        }

        // Ordinary editing keys (backspace, delete, arrows, home/end) are handled by the
        // base TextView, then mirrored back into the controller.
        var handled = base.OnKeyDown(key);
        this.SyncControllerFromTextView();
        this.RaiseLayoutInvalidated();
        return handled;
    }

    protected override bool OnPaste(string text)
    {
        // Startup disables input; a bracketed paste must be ignored just like a keystroke.
        if (!this.inputEnabled)
        {
            return true;
        }

        var handled = this.HandlePaste(text);
        this.RaiseCompletionIfChanged();
        return handled;
    }

    private bool HandlePaste(string text)
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
        this.RaiseLayoutInvalidated();
        return true;
    }

    /// <summary>
    /// Moves the caret one visual (wrapped) row using the laid-out map, carrying the preferred display
    /// column across the move, then remeasures and re-scrolls so the caret stays visible.
    /// </summary>
    private bool MoveVisual(ComposerVisualLayout layout, UiAction action)
    {
        var delta = action == UiAction.CursorVisualUp ? -1 : 1;
        var moved = layout.MoveVertical(
            this.controller.State.CursorIndex,
            delta,
            this.controller.State.PreferredDisplayColumn);
        this.controller.MoveCursorTo(moved.CursorIndex, moved.PreferredColumn);
        this.SyncTextView();
        this.ApplyViewport(Math.Max(1, this.Viewport.Width), Math.Max(1, this.Viewport.Height));
        return true;
    }

    private bool HandleAction(UiAction action)
    {
        switch (action)
        {
            case UiAction.Submit:
                var result = this.controller.Apply(UiAction.Submit);
                this.SyncTextView();
                this.RaiseLayoutInvalidated();
                if (result.SubmittedText is { } submitted)
                {
                    this.Submitted?.Invoke(this, submitted);
                }

                return true;

            case UiAction.InsertNewline:
            case UiAction.CompleteSuggestion:
            case UiAction.HistoryPrevious:
            case UiAction.HistoryNext:
            case UiAction.CursorLeft:
            case UiAction.CursorRight:
            case UiAction.WordLeft:
            case UiAction.WordRight:
            case UiAction.LineStart:
            case UiAction.LineEnd:
                // Content edits, completion, history, and caret movement can all change the measured height
                // or push the caret out of view, so remeasure and re-scroll.
                this.controller.Apply(action);
                this.SyncTextView();
                this.RaiseLayoutInvalidated();
                return true;

            case UiAction.CompletionPrevious:
            case UiAction.CompletionNext:
            case UiAction.DismissCompletion:
                // Completion selection/visibility changes neither the draft nor the caret, so they never
                // resize or rescroll the composer.
                this.controller.Apply(action);
                this.SyncTextView();
                return true;

            default:
                // Interrupt, Exit, ToggleMode, ForceRedraw, Open*, Transcript*, JumpToNewest.
                this.ActionRequested?.Invoke(this, action);
                return true;
        }
    }

    private void OnContentsChangedSync(object? sender, ContentsChangedEventArgs e)
    {
        // The base editor applies edits (backspace, delete, cut) through this event, often a beat after the
        // key is handled. Mirror the draft back and remeasure so the composer resizes/rescrolls for the
        // post-edit content; skip while we are the ones pushing text in (SyncTextView guards that).
        if (this.syncingText)
        {
            return;
        }

        this.SyncControllerFromTextView();
        this.RaiseLayoutInvalidated();
    }

    /// <summary>
    /// Fires <see cref="CompletionChanged"/> only when the completion's suggestion identities, selected
    /// index, or visibility actually differ from the last observed state, so repeated syncs and plain
    /// typing that leaves the completion unchanged never trigger a redraw loop.
    /// </summary>
    private void RaiseCompletionIfChanged()
    {
        var suggestions = this.controller.Suggestions;
        var selected = this.controller.SelectedSuggestionIndex;
        if (selected == this.lastSelectedIndex && SameSuggestions(this.lastSuggestions, suggestions))
        {
            return;
        }

        this.SnapshotCompletion();
        this.CompletionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SnapshotCompletion()
    {
        this.lastSuggestions = this.controller.Suggestions;
        this.lastSelectedIndex = this.controller.SelectedSuggestionIndex;
    }

    private static bool SameSuggestions(IReadOnlyList<ISlashCommand> a, IReadOnlyList<ISlashCommand> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

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

            var layout = ComposerVisualLayout.Create(state.Draft, this.LayoutWidth());
            var position = layout.PositionForIndex(state.CursorIndex);
            this.InsertionPoint = new System.Drawing.Point(position.Column, position.Row);
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
        var point = this.InsertionPoint;
        var layout = ComposerVisualLayout.Create(draft, this.LayoutWidth());
        var cursor = layout.IndexForPosition(point.Y, point.X);
        this.controller.ReplaceDraft(draft, cursor);
    }

    /// <summary>
    /// The display-cell width the composer wraps at: its laid-out viewport width, falling back to the frame
    /// width and finally to a single cell so a not-yet-laid-out view never divides by zero.
    /// </summary>
    private int LayoutWidth()
    {
        var width = this.Viewport.Width;
        if (width <= 0)
        {
            width = this.Frame.Width;
        }

        return Math.Max(1, width);
    }

    private void RaiseLayoutInvalidated() => this.LayoutInvalidated?.Invoke(this, EventArgs.Empty);

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

    /// <summary>
    /// A suggestion generator that never offers suggestions, used to keep the base <see cref="TextView"/>
    /// autocomplete inert so it never surfaces its own suggestions. The composer renders slash-command
    /// completion through the shell-owned command completion menu instead.
    /// </summary>
    private sealed class NoSuggestionGenerator : ISuggestionGenerator
    {
        public IEnumerable<Suggestion> GenerateSuggestions(AutocompleteContext context) => [];

        public bool IsWordChar(string text) => false;
    }
}

#pragma warning restore CS0618
