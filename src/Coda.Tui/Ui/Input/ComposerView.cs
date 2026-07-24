// The whole file opts out of CS0618 because Terminal.Gui 2.4.17 marks TextView as
// obsolete (superseded by an external Editor package), yet it remains the supported
// multiline editor in the released package. The suppression is intentionally scoped to
// this single file so no other production code silences the warning.
#pragma warning disable CS0618

using System.Text;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Input;

/// <summary>
/// Multiline Terminal.Gui composer built on <see cref="TextView"/>. Key handling is
/// routed through <see cref="UiActionMap"/> and a Terminal.Gui-independent
/// <see cref="ComposerController"/>, which owns the authoritative draft/caret/history
/// state; the <see cref="TextView"/> is kept as a synchronized mirror. Enter submits,
/// Shift+Enter inserts a newline (with Ctrl+Enter and Ctrl+J as terminal-compatible fallbacks), and
/// bracketed paste is inserted literally so embedded newlines can never trigger a submission.
/// </summary>
internal sealed class ComposerView : TextView
{
    private readonly ComposerController controller;
    private bool syncingText;
    private bool syncingNativeInput;
    private bool caretPlaced;
    private System.Drawing.Point lastUnwrappedPosition;
    private bool inputEnabled = true;
    private bool suppressLeftGesture;
    private bool suppressRightGesture;
    private bool pendingRightPaste;
    private IReadOnlyList<ISlashCommand> lastSuggestions = [];
    private int lastSelectedIndex = -1;

    /// <summary>
    /// Number of times the whole <see cref="TextView.Text"/> was reassigned. Only programmatic draft
    /// replacement (initial sync, SetDraft, Restore, history/completion swaps) should bump this; native
    /// printable/delete/paste edits mutate the model incrementally and must never replace the whole text.
    /// Exposed for tests only.
    /// </summary>
    internal int FullTextReplacementCount { get; private set; }

    static ComposerView()
    {
        // Terminal.Gui's base TextView constructor seeds every instance's Cursor.Style from the
        // process-global static TextView.DefaultCursorStyle (BlinkingBar in 2.4.17). Because ComposerView
        // is Coda's only TextView, overriding that global default to an always-visible block cursor is safe
        // and affects nothing else. Running it in the static constructor guarantees the block style is in
        // place before the first composer's base constructor reads it, and it is never touched per edit/frame.
        TextView.DefaultCursorStyle = CursorStyle.SteadyBlock;
    }

    public ComposerView(ComposerController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));

        // Re-assert the block default once per composer (never per edit/frame) so a composer built after some
        // other code changed the process-global default still uses it, and align this instance's cursor —
        // which the base constructor already seeded from whatever the default happened to be — without
        // touching the caret position or focus-driven visibility.
        TextView.DefaultCursorStyle = CursorStyle.SteadyBlock;
        this.Cursor = this.Cursor with { Style = CursorStyle.SteadyBlock };

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
        this.UnwrappedCursorPositionChanged += this.OnUnwrappedCursorPositionChanged;
        this.SyncTextView();
        this.SnapshotCompletion();
    }

    /// <summary>Raised when the user submits the composer; carries the submitted text.</summary>
    public event EventHandler<string>? Submitted;

    /// <summary>
    /// Raised when the user submits the composer; preserves the original draft so shell interceptors can
    /// distinguish typed intent from text produced by case-insensitive completion.
    /// </summary>
    internal event EventHandler<ComposerSubmissionEventArgs>? SubmissionSubmitted;

    /// <summary>Raised for shell-level actions (interrupt, exit, toggle mode, transcript scrolling, ...).</summary>
    public event EventHandler<UiAction>? ActionRequested;

    /// <summary>
    /// Raised when a pointer gesture over the composer resolves to a semantic clipboard/context action —
    /// copy the current selection, paste at the caret, or show the context menu. The composer only classifies
    /// the gesture (using the native <see cref="TextView"/> selection and caret); the shell performs the
    /// clipboard I/O and menu presentation. See <see cref="ComposerPointerActionRequestedEventArgs"/>.
    /// </summary>
    internal event EventHandler<ComposerPointerActionRequestedEventArgs>? PointerActionRequested;

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
    /// Whether the composer currently has a non-empty native <see cref="TextView"/> selection. Backed by the
    /// real selection length so it tracks whatever the base editor selected via keyboard or mouse drag.
    /// </summary>
    internal bool HasComposerSelection => this.SelectedLength > 0;

    /// <summary>The native <see cref="TextView.SelectedText"/>, or an empty string when nothing is selected.</summary>
    internal string SelectedComposerText => this.SelectedText ?? string.Empty;

    /// <summary>
    /// Whether a left copy gesture has armed suppression of the remainder of its click sequence. It is set on
    /// the copy press and cleared by the gesture's terminal click; it must never remain armed after the
    /// gesture completes. Exposed for tests only so the completion contract can be asserted directly.
    /// </summary>
    internal bool LeftGestureSuppressed => this.suppressLeftGesture;

    /// <summary>
    /// Clears only the native selection highlight and repaints. It never mutates the draft text or moves the
    /// caret, so the shell can drop a selection (e.g. after a copy) without disturbing the edit position.
    /// </summary>
    internal void ClearComposerSelection()
    {
        this.IsSelecting = false;
        this.SetNeedsDraw();
    }

    /// <summary>
    /// An optional shell-level key handler consulted before the composer's own key handling (but after the
    /// startup-disabled guard). The shell uses it to claim keys it must own regardless of focus; when it
    /// returns true the composer treats the key as handled and does nothing further.
    /// </summary>
    internal Func<Key, bool>? ShellKeyHandler { get; set; }

    /// <summary>
    /// Shell/controller seam for Up at an empty top-row composer. A non-null returned draft consumes the key;
    /// null preserves normal prompt-history navigation.
    /// </summary>
    internal Func<string?>? RecallPendingSteering { get; set; }

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

        this.InsertEdit(text);
        this.RaiseCompletionIfChanged();
        this.RaiseLayoutInvalidated();
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
    /// count clamped to <c>[1, MaximumHeight(screenHeight)]</c>. A draft that fits on one visual line uses a
    /// single content row; it only grows when explicit newlines or wrapping need more visual rows.
    /// </summary>
    internal int DesiredHeight(int width, int screenHeight)
    {
        var visualRows = this.MeasureLayout(width).VisualLineCount;
        return Math.Min(MaximumHeight(screenHeight), Math.Max(1, visualRows));
    }

    /// <summary>
    /// Re-measures the draft and scrolls the internal viewport so the caret's visual row stays within the
    /// visible <paramref name="height"/> rows, recording the resulting scroll row on the controller. It
    /// never reassigns <see cref="TextView.InsertionPoint"/>: the native editor owns the caret, so a shell
    /// layout pass or a native edit must not stomp it with a Coda-computed wrapped position.
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
    }

    /// <summary>
    /// Injects a paste payload the same way the driver would deliver a bracketed paste.
    /// Public so tests can exercise paste handling without a live console driver.
    /// </summary>
    public bool NewPasteEvent(string text) => this.OnPaste(text ?? string.Empty);

    /// <inheritdoc />
    protected override void OnSubViewsLaidOut(LayoutEventArgs args)
    {
        base.OnSubViewsLaidOut(args);

        // The constructor's caret placement is a no-op on an unlaid-out view (the base editor reports its
        // insertion point as the origin), so place the caret from the controller once the editor is first
        // laid out. Later layout passes must not re-place it — the native editor owns the caret after edits.
        if (!this.caretPlaced && this.IsInitialized)
        {
            this.caretPlaced = true;
            this.SyncTextView();
        }
    }

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

    /// <summary>
    /// Arbitrates a mouse event between semantic composer actions and native editing. Gestures are first
    /// classified into semantic pointer actions (copy, paste, context menu) by
    /// <see cref="TryHandlePointerGesture"/>; anything not owned there falls through to the base
    /// <see cref="TextView"/>, which positions the caret from a mouse click/drag. That native move is mirrored
    /// into the controller — the caret source of truth. The base editor owns caret positioning for the mouse,
    /// so rather than mapping the raw pointer x/y through the Coda wrap (which can diverge from the editor's own
    /// wrapping), the caret it settles on raises <c>UnwrappedCursorPositionChanged</c> and — because
    /// <see cref="syncingNativeInput"/> is set for the duration — is mirrored from its wrap-independent
    /// unwrapped position back to a UTF-16 index. That keeps the controller caret on exactly the clicked
    /// character across soft/hard word-wrap, so a following Delete removes it instead of a stale one and the
    /// caret never snaps to the first visual row. A layout pass never routes through here, so it can never
    /// corrupt the controller.
    /// </summary>
    protected override bool OnMouseEvent(Mouse mouse)
    {
        // Startup disables input; swallow mouse input just like keys so a click can never edit or move the
        // caret while initialization is in flight.
        if (!this.inputEnabled)
        {
            return true;
        }

        if (this.TryHandlePointerGesture(mouse, out var gestureHandled))
        {
            return gestureHandled;
        }

        // Every unmatched event continues through the native path so the mouse-caret synchronization fix
        // (UnwrappedCursorPositionChanged mirroring under syncingNativeInput) stays intact.
        var handled = false;
        this.RunNativeEdit(() => handled = base.OnMouseEvent(mouse));
        return handled;
    }

    /// <summary>
    /// Classifies a pointer gesture into a semantic composer action (copy/paste/context menu) or lets it fall
    /// through to the native editor. Terminal.Gui delivers a button gesture as press, release, then a
    /// synthesized clicked event (drags interleave <see cref="MouseFlags.PositionReport"/> moves); the small
    /// state machine below consumes the remainder of a gesture once it has been claimed so a single physical
    /// action never yields a duplicate. Returns true (with <paramref name="handled"/> set) when the gesture is
    /// owned here; false to defer to the native path.
    /// </summary>
    private bool TryHandlePointerGesture(Mouse mouse, out bool handled)
    {
        handled = true;
        var flags = mouse.Flags;

        // A copy was already raised on the left press; swallow the rest of the sequence so the release/click
        // can't start a fresh native drag after the shell clears the old selection. Terminal.Gui delivers the
        // gesture as press, release, then a synthesized click, so suppression must survive the release and only
        // lift on that terminal click — otherwise the trailing click leaks through to the base editor and
        // repositions the caret. A second or third physical click reports the distinct
        // LeftButtonDoubleClicked / LeftButtonTripleClicked bit, so all three complete the armed gesture
        // (via IsLeftGestureCompletion) — otherwise a multi-click would leave suppression armed until the next
        // press had to recover it, swallowing native events in between.
        if (this.suppressLeftGesture)
        {
            if (IsGestureStartingPress(flags))
            {
                // A truncated / off-view / grab-loss sequence can end without the terminal synthesized click
                // that would otherwise lift suppression, leaving it armed forever; a fresh press begins a new
                // gesture, so recover and reinterpret it here.
                this.suppressLeftGesture = false;
            }
            else
            {
                if (IsLeftGestureCompletion(flags))
                {
                    this.suppressLeftGesture = false;
                }

                return true;
            }
        }

        // A copy was already raised on the right press; swallow the complete right gesture through its
        // terminal synthesized click so it can never reach the native context menu.
        if (this.suppressRightGesture)
        {
            if (IsGestureStartingPress(flags))
            {
                this.suppressRightGesture = false;
            }
            else
            {
                if (IsRightGestureCompletion(flags))
                {
                    this.suppressRightGesture = false;
                }

                return true;
            }
        }

        // The caret was positioned on the right press; raise exactly one paste when the click completes and
        // consume the release/click in between. A gesture may terminate with any of the distinct
        // RightButtonClicked / RightButtonDoubleClicked / RightButtonTripleClicked bits (a second physical
        // click reports the double-click bit, and so on), so all three complete the armed gesture — otherwise a
        // double right-click would leave this armed, swallow later events, and fire a stale paste. A fresh
        // gesture-starting press instead recovers a pending paste that a truncated / off-view / grab-loss
        // sequence left armed, and falls through so the new press is reinterpreted.
        if (this.pendingRightPaste)
        {
            if (IsGestureStartingPress(flags))
            {
                this.pendingRightPaste = false;
            }
            else
            {
                if (IsRightGestureCompletion(flags))
                {
                    this.pendingRightPaste = false;
                    this.RaisePointerAction(ComposerPointerActionKind.PasteClipboard, null, mouse.ScreenPosition);
                }

                return true;
            }
        }

        // A fresh, unshifted left press over an existing selection copies it and consumes the click sequence
        // instead of starting another drag. PositionReport identifies a drag move (not a new press), which
        // must keep extending the native selection.
        if (flags.HasFlag(MouseFlags.LeftButtonPressed)
            && !flags.HasFlag(MouseFlags.PositionReport)
            && !flags.HasFlag(MouseFlags.Shift)
            && this.HasComposerSelection)
        {
            this.suppressLeftGesture = true;
            this.RaisePointerAction(
                ComposerPointerActionKind.CopySelection, this.SelectedComposerText, mouse.ScreenPosition);
            return true;
        }

        // A right press over a selection copies and consumes the gesture; a right press without one positions
        // the caret natively and defers a single paste to the click.
        if (flags.HasFlag(MouseFlags.RightButtonPressed) && !flags.HasFlag(MouseFlags.PositionReport))
        {
            if (this.HasComposerSelection)
            {
                this.suppressRightGesture = true;
                this.RaisePointerAction(
                    ComposerPointerActionKind.CopySelection, this.SelectedComposerText, mouse.ScreenPosition);
                return true;
            }

            this.pendingRightPaste = true;
            this.PositionCaretFromPress(mouse);
            return true;
        }

        // Middle click surfaces the context menu at the pointer, exactly once.
        if (flags.HasFlag(MouseFlags.MiddleButtonClicked))
        {
            this.RaisePointerAction(ComposerPointerActionKind.ShowContextMenu, null, mouse.ScreenPosition);
            return true;
        }

        handled = false;
        return false;
    }

    /// <summary>
    /// True when <paramref name="flags"/> mark the fresh start of a new pointer gesture — a button press that
    /// is not a drag move (<see cref="MouseFlags.PositionReport"/>). Used to recover an armed suppression or
    /// pending paste that a truncated / off-view / grab-loss sequence — one that never delivered its terminal
    /// synthesized click — would otherwise leave armed forever, so the next real gesture is reinterpreted
    /// instead of swallowed.
    /// </summary>
    private static bool IsGestureStartingPress(MouseFlags flags) =>
        !flags.HasFlag(MouseFlags.PositionReport)
        && (flags.HasFlag(MouseFlags.LeftButtonPressed)
            || flags.HasFlag(MouseFlags.RightButtonPressed)
            || flags.HasFlag(MouseFlags.MiddleButtonPressed));

    /// <summary>
    /// True when <paramref name="flags"/> carry a terminal completion event for an armed right gesture.
    /// Terminal.Gui reports the first physical click as <see cref="MouseFlags.RightButtonClicked"/>, the second
    /// as <see cref="MouseFlags.RightButtonDoubleClicked"/>, and the third as
    /// <see cref="MouseFlags.RightButtonTripleClicked"/>; each is a single distinct bit for one physical click,
    /// so testing all three gives consistent completion semantics and never double-counts a lone terminal bit.
    /// </summary>
    private static bool IsRightGestureCompletion(MouseFlags flags) =>
        flags.HasFlag(MouseFlags.RightButtonClicked)
        || flags.HasFlag(MouseFlags.RightButtonDoubleClicked)
        || flags.HasFlag(MouseFlags.RightButtonTripleClicked);

    /// <summary>
    /// True when <paramref name="flags"/> carry a terminal completion event for an armed left gesture, the
    /// mirror of <see cref="IsRightGestureCompletion"/>. Terminal.Gui reports the first physical click as
    /// <see cref="MouseFlags.LeftButtonClicked"/>, the second as <see cref="MouseFlags.LeftButtonDoubleClicked"/>,
    /// and the third as <see cref="MouseFlags.LeftButtonTripleClicked"/>; each is a single distinct bit for one
    /// physical click, so testing all three completes the armed copy gesture on any of them and never leaves
    /// suppression stuck armed after a multi-click.
    /// </summary>
    private static bool IsLeftGestureCompletion(MouseFlags flags) =>
        flags.HasFlag(MouseFlags.LeftButtonClicked)
        || flags.HasFlag(MouseFlags.LeftButtonDoubleClicked)
        || flags.HasFlag(MouseFlags.LeftButtonTripleClicked);

    /// <summary>
    /// Positions the native caret from a right press by replaying it as a self-contained left press-and-release
    /// through the base <see cref="TextView"/>, inside the <see cref="RunNativeEdit"/> synchronization guard so
    /// the resulting <c>UnwrappedCursorPositionChanged</c> mirrors the clicked wrapped position into the
    /// controller. Reusing the native handler avoids manually translating wrapped coordinates.
    ///
    /// The base editor grabs the application mouse on the left press (so a real drag keeps receiving motion),
    /// so the caret positioning must hand that grab straight back: the matching synthetic release ungrabs it
    /// within the same scope. Without it the composer would keep the global grab, silently rerouting the
    /// transcript's real mouse events to the composer. A press-then-release at one position only moves the
    /// caret — it selects nothing and raises no semantic action — so caret/controller state is unchanged
    /// beyond the intended reposition.
    /// </summary>
    private void PositionCaretFromPress(Mouse mouse)
    {
        var press = new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = mouse.Position,
            ScreenPosition = mouse.ScreenPosition,
            View = mouse.View,
        };

        var release = new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = mouse.Position,
            ScreenPosition = mouse.ScreenPosition,
            View = mouse.View,
        };

        this.RunNativeEdit(() =>
        {
            base.OnMouseEvent(press);
            base.OnMouseEvent(release);
        });
    }

    private void RaisePointerAction(
        ComposerPointerActionKind kind, string? selectedText, System.Drawing.Point screenPosition) =>
        this.PointerActionRequested?.Invoke(
            this, new ComposerPointerActionRequestedEventArgs(kind, selectedText, screenPosition));

    private bool HandleKeyDown(Key key)
    {
        // A previous key's base binding may have edited the model outside our handlers; reconcile before
        // mapping this key so it acts on the editor's true draft and caret.
        this.ReconcileFromEditorIfContentDrifted();

        var layout = this.MeasureLayout(Math.Max(1, this.Viewport.Width));
        var caret = layout.PositionForIndex(this.controller.State.CursorIndex);
        var context = new UiInputContext(
            ComposerEmpty: this.controller.State.Draft.Length == 0,
            CompletionVisible: this.controller.Suggestions.Count > 0,
            CanMoveVisualUp: caret.Row > 0,
            CanMoveVisualDown: caret.Row < layout.VisualLineCount - 1);
        var action = UiActionMap.Map(key, context);

        if (action == UiAction.HistoryPrevious &&
            key == Key.CursorUp &&
            context.ComposerEmpty &&
            !context.CompletionVisible &&
            !context.CanMoveVisualUp &&
            this.RecallPendingSteering?.Invoke() is { } recalledDraft)
        {
            this.SetDraft(recalledDraft, recalledDraft.Length);
            return true;
        }

        // While a paste is in progress, a stray Enter is text, never a submission (with or without an open
        // completion).
        if (this.controller.State.PasteActive && action is UiAction.Submit or UiAction.CompleteAndSubmit)
        {
            action = UiAction.InsertNewline;
        }

        if (action is UiAction.CursorVisualUp or UiAction.CursorVisualDown)
        {
            return this.MoveVisual(layout, action);
        }

        // Native text edits (printable characters, backspace, delete): once the editor is laid out, apply
        // them incrementally through the base TextView so the wrapped model is only nudged, never rebuilt
        // from a whole-text assignment. The edit raises ContentsChanged (draft) and
        // UnwrappedCursorPositionChanged (caret); mirroring the caret from the wrap-independent unwrapped
        // position — rather than reconstructing it from wrapped coordinates through the Coda layout — is what
        // keeps typing and deletion near a soft-wrap boundary from swapping word fragments or snapping the
        // caret to row 0.
        if (action == UiAction.None && this.IsInitialized && this.TryNativeEdit(key))
        {
            return true;
        }

        if (action != UiAction.None)
        {
            return this.HandleAction(action);
        }

        // A key with no mapped action and no native edit (an unbound key, or any edit before layout): let the
        // base binding attempt it, then mirror the draft from the model.
        return this.HandleUnmappedKey(key);
    }

    /// <summary>
    /// Applies a native incremental edit for <paramref name="key"/> — printable insertion, backspace, or
    /// delete — through the base <see cref="TextView"/>, with <see cref="syncingNativeInput"/> set so the
    /// resulting ContentsChanged/UnwrappedCursorPositionChanged events mirror the edit into the controller.
    /// Returns false when the key is not a native edit.
    /// </summary>
    private bool TryNativeEdit(Key key)
    {
        if (TryGetPrintableText(key, out var text))
        {
            this.RunNativeEdit(() => this.InsertText(text));
            this.RaiseLayoutInvalidated();
            return true;
        }

        if (key == Key.Backspace)
        {
            this.RunNativeEdit(() => this.NativeDeleteAtControllerCaret(forward: false));
            this.RaiseLayoutInvalidated();
            return true;
        }

        if (key == Key.Delete)
        {
            this.RunNativeEdit(() => this.NativeDeleteAtControllerCaret(forward: true));
            this.RaiseLayoutInvalidated();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Performs a native backspace/forward-delete at the controller's authoritative caret index. The base
    /// <see cref="TextView"/> deletes at its own caret, but its wrapped <see cref="TextView.InsertionPoint"/>
    /// coordinates diverge from the composer's grapheme layout once a logical line soft-wraps, so a delete
    /// after a programmatic (SetDraft/arrow/history) caret placement would otherwise remove the wrong
    /// character or drift the caret. Toggling <see cref="TextView.WordWrap"/> off makes InsertionPoint a
    /// direct (grapheme-column, logical-row) address that round-trips exactly; the caret is placed there and
    /// the delete runs before WordWrap is restored, so exactly the character at the caret is removed and the
    /// caret never snaps to the first visual row.
    /// </summary>
    private void NativeDeleteAtControllerCaret(bool forward)
    {
        var draft = this.controller.State.Draft;
        var index = Math.Clamp(this.controller.State.CursorIndex, 0, draft.Length);
        var (row, column) = LogicalCaret(draft, index);
        var previousWrap = this.WordWrap;

        // Reposition the base caret via logical coordinates without mirroring the (unchanged) draft/caret,
        // then perform the delete as real native input so its ContentsChanged/UnwrappedCursorPositionChanged
        // events mirror the result back to the controller.
        this.syncingText = true;
        try
        {
            this.WordWrap = false;
            this.InsertionPoint = new System.Drawing.Point(column, row);
        }
        finally
        {
            this.syncingText = false;
        }

        try
        {
            if (forward)
            {
                this.DeleteCharRight();
            }
            else
            {
                this.DeleteCharLeft();
            }
        }
        finally
        {
            this.WordWrap = previousWrap;
        }

        // The delete ran with WordWrap off and the toggle back on leaves the base editor's visible caret at
        // the origin; restore it to the wrapped position for the (already-correct) controller caret.
        this.PlaceDisplayCaretFromLayout();
    }

    /// <summary>
    /// The base editor's unwrapped caret address for <paramref name="index"/>: the logical (newline-delimited)
    /// row and the grapheme-cluster column within that row, matching the coordinates
    /// <see cref="SourceIndexFromUnwrappedPosition"/> maps back from.
    /// </summary>
    private static (int Row, int Column) LogicalCaret(string text, int index)
    {
        var row = 0;
        var lineStart = 0;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                row++;
                lineStart = i + 1;
            }
        }

        var column = 0;
        foreach (var _ in TerminalCellText.Enumerate(text[lineStart..index]))
        {
            column++;
        }

        return (row, column);
    }

    private bool HandleUnmappedKey(Key key)
    {
        if (!this.IsInitialized && TryGetPrintableText(key, out var text))
        {
            // Headless construction (before the shell begins): the base editing surface cannot track a caret,
            // so drive the controller directly and mirror the text programmatically.
            this.controller.InsertText(text);
            this.SyncTextView();
            this.RaiseLayoutInvalidated();
            return true;
        }

        // An unbound key: let the base binding attempt an edit, then mirror the draft from the model. The
        // caret is corrected by the next native edit or programmatic sync.
        var handled = base.OnKeyDown(key);
        if (!this.syncingText)
        {
            this.controller.SetDraftText(NormalizeNewlines(this.Text ?? string.Empty));
            this.RaiseLayoutInvalidated();
        }

        return handled;
    }

    /// <summary>
    /// Runs a native edit with <see cref="syncingNativeInput"/> set so the base editor's ContentsChanged and
    /// UnwrappedCursorPositionChanged events are honored as real user input (and not confused with the
    /// spurious caret reports a layout pass emits).
    /// </summary>
    private void RunNativeEdit(Action edit)
    {
        var previous = this.syncingNativeInput;
        this.syncingNativeInput = true;
        try
        {
            edit();
        }
        finally
        {
            this.syncingNativeInput = previous;
        }
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
        var payload = NormalizeNewlines(text ?? string.Empty);

        // Guard against a submission mid-paste; the driver delivers the payload as one OnPaste call, but a
        // trailing Enter that arrives while PasteActive is text, never a submit.
        this.controller.BeginPaste();
        try
        {
            if (this.IsInitialized)
            {
                // Native incremental paste at the editor caret; ContentsChanged/UnwrappedCursorPositionChanged
                // mirror the inserted text and caret into the controller.
                this.RunNativeEdit(() => base.OnPaste(payload));
            }
            else
            {
                this.controller.InsertText(payload);
                this.SyncTextView();
            }
        }
        finally
        {
            this.controller.EndPaste();
        }

        this.RaiseLayoutInvalidated();
        return true;
    }

    /// <summary>
    /// Inserts <paramref name="text"/> at the caret. Once laid out this uses the base
    /// <see cref="TextView.InsertText(string)"/> so the model mutates incrementally (and the sync events
    /// mirror it); before layout it drives the controller and mirrors the text programmatically.
    /// </summary>
    private void InsertEdit(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (this.IsInitialized)
        {
            this.RunNativeEdit(() => this.InsertText(text));
        }
        else
        {
            this.controller.InsertText(text);
            this.SyncTextView();
        }
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
            case UiAction.CompleteAndSubmit:
                // Submit (and, for CompleteAndSubmit, first accept the selected completion) replaces the whole
                // draft, so re-place the caret and remeasure. Exactly one Submitted fires — CompleteAndSubmit
                // returns a single submission result, never a separate completion then submission.
                var originalDraft = this.controller.State.Draft;
                var result = this.controller.Apply(action);
                this.SyncTextView();
                this.RaiseLayoutInvalidated();
                if (result.SubmittedText is { } submitted)
                {
                    this.Submitted?.Invoke(this, submitted);
                    this.SubmissionSubmitted?.Invoke(
                        this,
                        new ComposerSubmissionEventArgs(submitted, originalDraft));
                }

                return true;

            case UiAction.InsertNewline:
                // A newline is a native incremental insert (via InsertText), not a whole-text replacement.
                this.InsertEdit("\n");
                this.RaiseLayoutInvalidated();
                return true;

            case UiAction.CompleteSuggestion:
            case UiAction.HistoryPrevious:
            case UiAction.HistoryNext:
                // Completion and history swap the whole draft, so replace the text and re-place the caret,
                // then remeasure and re-scroll for the new content.
                this.controller.Apply(action);
                this.SyncTextView();
                this.RaiseLayoutInvalidated();
                return true;

            case UiAction.CursorLeft:
            case UiAction.CursorRight:
            case UiAction.WordLeft:
            case UiAction.WordRight:
            case UiAction.LineStart:
            case UiAction.LineEnd:
                // Horizontal caret movement is Coda-driven so the caret source of truth moves immediately and
                // a subsequent insert/paste lands at the moved position. The caret is re-placed once (via
                // SyncTextView); native layout passes never reassign it.
                this.controller.Apply(action);
                this.SyncTextView();
                this.RaiseLayoutInvalidated();
                return true;

            case UiAction.CompletionPrevious:
            case UiAction.CompletionNext:
            case UiAction.DismissCompletion:
                // Completion selection/visibility changes neither the draft nor the caret, so they never
                // resize/rescroll the composer and must not reassign the native caret.
                this.controller.Apply(action);
                return true;

            default:
                // Interrupt, Exit, ToggleMode, ForceRedraw, Open*, Transcript*, JumpToNewest.
                this.ActionRequested?.Invoke(this, action);
                return true;
        }
    }

    private void OnContentsChangedSync(object? sender, ContentsChangedEventArgs e)
    {
        // A native edit (base key binding, InsertText, or paste) mutated the wrapped model. Mirror the draft
        // text — the caret is mirrored separately by the unwrapped cursor event — and remeasure so the
        // composer resizes/rescrolls. Skip while we are the ones pushing text in (SyncTextView guards that).
        if (this.syncingText)
        {
            return;
        }

        this.controller.SetDraftText(NormalizeNewlines(this.Text ?? string.Empty));
        this.RaiseCompletionIfChanged();
        this.RaiseLayoutInvalidated();
    }

    private void OnUnwrappedCursorPositionChanged(object? sender, System.Drawing.Point unwrapped)
    {
        // Remember the editor's latest caret so a subsequent key can reconcile the controller after a base
        // binding edited the model outside our own handlers (see ReconcileFromEditorIfContentDrifted).
        this.lastUnwrappedPosition = unwrapped;

        // A native edit updated the editor caret. Mirror it to the controller using the wrap-independent
        // unwrapped position (logical row + grapheme column) so soft-wrap divergence can never swap word
        // fragments or snap the caret to the first line. Honour it only during genuine native input (a native
        // edit or a mouse positioning, both of which set syncingNativeInput): while we drive the caret
        // programmatically (SyncTextView) or a layout pass emits a spurious caret report, the controller
        // stays authoritative.
        if (this.syncingText || !this.syncingNativeInput)
        {
            return;
        }

        var index = SourceIndexFromUnwrappedPosition(this.controller.State.Draft, unwrapped);
        this.controller.MoveCursorTo(index);
        this.RaiseCompletionIfChanged();
    }

    /// <summary>
    /// Reconciles the controller when a previous key's base <see cref="TextView"/> binding (e.g.
    /// delete-word-left) changed the model outside the composer's own edit handlers. Such bindings run in the
    /// command pipeline after <see cref="OnKeyDown"/> returns, and some mutate the text without raising
    /// ContentsChanged, so the controller draft can drift. Runs only on real key input (never during a layout
    /// pass), mirrors the current text, and maps the caret from the editor's last reported unwrapped position
    /// — so a following Coda-driven caret move can never restore text the editor already deleted.
    /// </summary>
    private void ReconcileFromEditorIfContentDrifted()
    {
        if (!this.IsInitialized || this.syncingText)
        {
            return;
        }

        var draft = NormalizeNewlines(this.Text ?? string.Empty);
        if (string.Equals(draft, this.controller.State.Draft, StringComparison.Ordinal))
        {
            return;
        }

        this.controller.SetDraftText(draft);
        this.controller.MoveCursorTo(SourceIndexFromUnwrappedPosition(draft, this.lastUnwrappedPosition));
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
    /// Mirrors the controller's draft and caret into the base editor for a programmatic change (initial
    /// sync, SetDraft, Restore, submit/newline, history, completion, visual movement). The whole
    /// <see cref="TextView.Text"/> is reassigned only when it actually differs (counted for tests), and the
    /// caret is placed once from the Coda layout. Native edit/layout passes never call this — they let the
    /// base editor own the caret.
    /// </summary>
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
                this.FullTextReplacementCount++;
            }

            this.PlaceDisplayCaretFromLayout();
        }
        finally
        {
            this.syncingText = false;
        }
    }

    /// <summary>
    /// Places the base editor's <em>displayed</em> caret at the Coda layout's wrapped position for the
    /// controller's caret index, without reassigning <see cref="TextView.Text"/> (so it never counts as a
    /// full replacement). Used after a native delete to restore the visible caret — the base editor otherwise
    /// leaves it at the origin once <see cref="TextView.WordWrap"/> is toggled back on. The controller's caret
    /// (mirrored from the delete's unwrapped event) stays authoritative because the assignment runs under
    /// <see cref="syncingText"/>, so the resulting caret report is not mirrored back.
    /// </summary>
    private void PlaceDisplayCaretFromLayout()
    {
        var state = this.controller.State;
        var layout = ComposerVisualLayout.Create(state.Draft, this.LayoutWidth());
        var position = layout.PositionForIndex(state.CursorIndex);
        var previous = this.syncingText;
        this.syncingText = true;
        try
        {
            this.InsertionPoint = new System.Drawing.Point(position.Column, position.Row);
        }
        finally
        {
            this.syncingText = previous;
        }
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
    /// Maps a Terminal.Gui unwrapped cursor position — a logical newline row and a grapheme column that is
    /// independent of soft word-wrap — onto a UTF-16 index into <paramref name="text"/>. Combining marks
    /// and multi-code-unit emoji count as a single column, matching the editor's unwrapped model. Clamped
    /// to valid bounds.
    /// </summary>
    internal static int SourceIndexFromUnwrappedPosition(string text, System.Drawing.Point unwrapped)
    {
        text ??= string.Empty;
        var targetRow = Math.Max(0, unwrapped.Y);
        var targetColumn = Math.Max(0, unwrapped.X);

        // Advance to the start of the requested logical (newline-delimited) row; a row past the last line
        // clamps to the end of the text.
        var lineStart = 0;
        for (var row = 0; row < targetRow; row++)
        {
            var newline = text.IndexOf('\n', lineStart);
            if (newline < 0)
            {
                return text.Length;
            }

            lineStart = newline + 1;
        }

        var lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        // Walk targetColumn grapheme clusters into the line; a column past the last grapheme clamps to the
        // line end.
        var offset = 0;
        var consumed = 0;
        foreach (var element in TerminalCellText.Enumerate(text[lineStart..lineEnd]))
        {
            if (consumed >= targetColumn)
            {
                break;
            }

            offset += element.Utf16Length;
            consumed++;
        }

        return Math.Clamp(lineStart + offset, 0, text.Length);
    }

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
