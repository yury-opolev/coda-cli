using System.Text;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;
using Coda.Tui.Ui.Tasks;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// Shared behavior for the Terminal.Gui shells: it hosts the composer, the single-line status label,
/// and the keyboard-only <see cref="PromptOverlay"/>; forwards composer submissions and named actions
/// to the shell's owner; and applies snapshots on the UI thread (only touching the status/overlay when
/// something actually changed). Concrete shells supply their own layout and transcript presentation.
/// </summary>
/// <remarks>
/// Only <see cref="ComposerState"/> ever crosses the shell boundary (during a mode switch). All other
/// presentation state — focused view, open prompt id, and the shell's transcript coordinates — stays
/// private to the shell layer and never enters <see cref="UiSessionSnapshot"/>, so no Terminal.Gui
/// type leaks into the host-neutral state model.
/// </remarks>
internal abstract class TerminalGuiShellBase : Window, IUiFrameSink, ITuiShellHandle
{
    private const int DefaultStatusWidth = 80;

    /// <summary>
    /// The <see cref="ActiveOperation.Kind"/> published by the controller while the interactive startup
    /// is running. While an operation of this kind is active the composer is hidden and disabled.
    /// </summary>
    private const string StartupOperationKind = "startup";

    private readonly IApplication app;
    private readonly ComposerController controller;
    private readonly IUiEventPublisher publisher;
    private readonly Func<UiSessionSnapshot, int, string> statusProjection;
    private readonly ToolDisplayMode toolDisplayMode;
    private readonly Func<bool> hasActiveWork;
    private readonly ShellCommandChordState chords;
    private readonly Func<TimeSpan, Func<bool>, object> addTimeout;
    private readonly Func<object, bool> removeTimeout;
    private readonly Func<string, bool> clipboardWriter;
    private readonly Func<ClipboardReadResult> clipboardReader;
    private object? chordTimeout;
    private object? transientOperationalTimeout;
    private object? composerLayoutTimeout;
    private OperationalStatus? transientOperationalOverride;

    private string? statusText;
    private int statusUpdateCount;
    private bool composerDisabled;
    private bool composerLockedByAttachment;
    private readonly TaskBrowserController? taskController;
    private readonly TaskBrowserOverlay? taskOverlay;
    private bool disposed;

    /// <summary>
    /// The exact set of sub-views this shell adds in <see cref="BuildLayout"/>. Any sub-view outside this
    /// set (the base <see cref="TextView"/> autocomplete popup, which appends itself to the running
    /// top-level on the first edit) is stripped so the fixed insertion order is preserved and the modal
    /// <see cref="PromptOverlay"/> always stays the topmost sub-view.
    /// </summary>
    private readonly HashSet<View> ownedSubViews;

    protected TerminalGuiShellBase(
        IApplication app,
        ComposerController controller,
        IUiEventPublisher publisher,
        UiSessionSnapshot initialSnapshot,
        Func<bool>? hasActiveWork = null,
        TimeProvider? timeProvider = null,
        Func<string, bool>? clipboardWriter = null,
        Func<ClipboardReadResult>? clipboardReader = null,
        Func<TimeSpan, Func<bool>, object>? addTimeout = null,
        Func<object, bool>? removeTimeout = null,
        TuiTheme? theme = null,
        Func<UiSessionSnapshot, int, string>? statusProjection = null,
        Func<TaskBrowserProvider?>? taskBrowserProvider = null,
        ToolDisplayMode toolDisplayMode = ToolDisplayMode.Tiny)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.Snapshot = initialSnapshot ?? throw new ArgumentNullException(nameof(initialSnapshot));
        this.statusProjection = statusProjection ?? StatusProjector.Project;
        this.toolDisplayMode = toolDisplayMode;
        this.hasActiveWork = hasActiveWork ?? (() => false);
        this.TimeSource = timeProvider ?? TimeProvider.System;
        this.clipboardWriter = clipboardWriter ??
            (text => this.app.Clipboard?.TrySetClipboardData(text) == true);
        this.clipboardReader = clipboardReader ?? this.ReadApplicationClipboard;
        this.Theme = theme ?? TuiTheme.WarmEmber;

        // The chord clock and the timeout seams drive the deterministic Esc/Ctrl+C chords: the same
        // add/remove-timeout delegates the operational row uses (defaulting to the application's own timer)
        // so tests can expire an armed chord without a running loop.
        this.chords = new ShellCommandChordState(this.TimeSource);
        this.addTimeout = addTimeout ?? ((time, callback) => app.AddTimeout(time, callback)!);
        this.removeTimeout = removeTimeout ?? app.RemoveTimeout;

        this.Composer = new ComposerView(controller);
        this.Chrome = new ComposerChromeView(this.Theme);
        this.Operational = new OperationalStatusView(app, this.Theme, addTimeout, removeTimeout);
        this.Status = new Label { CanFocus = false };
        this.PromptOverlay = new PromptOverlay(publisher, this.Theme);
        this.PromptOverlay.ApplyTheme(app.Driver);
        this.Completion = new CommandCompletionView(this.Theme);

        // Build the browser controller + hidden overlay before BuildLayout so the concrete shell can add the
        // overlay to its view tree. A null provider means no browser is hosted at all (e.g. non-interactive
        // callers, or the pre-first-turn state where the live TaskManager does not yet exist).
        if (taskBrowserProvider is not null)
        {
            this.taskController = new TaskBrowserController(taskBrowserProvider, this.TimeSource);
            this.taskOverlay = new TaskBrowserOverlay(this.app, this.taskController, this.Theme, this.OnTaskBrowserChanged);
        }

        // The composer routes every key through the shell first so the interrupt/exit chords win over the
        // composer's own printable/action mapping regardless of which view currently holds focus.
        this.Composer.ShellKeyHandler = this.TryHandleShellKey;

        this.Composer.Submitted += this.OnComposerSubmitted;
        this.Composer.ActionRequested += this.OnComposerActionRequested;
        this.Composer.PointerActionRequested += this.OnComposerPointerActionRequested;
        this.Composer.CompletionChanged += this.OnCompletionChanged;
        this.Composer.LayoutInvalidated += this.OnComposerLayoutInvalidatedHandler;
        this.Initialized += this.OnShellInitialized;

        this.BuildLayout();
        this.ownedSubViews = [.. this.SubViews];
        this.SyncCompletion();
        this.UpdateProjectedOperationalStatus(this.Snapshot);
        this.UpdateComposerAvailability(this.Snapshot);
    }

    /// <summary>Raised when the composer submits a prompt; carries the submitted text.</summary>
    public event EventHandler<string>? PromptSubmitted;

    /// <summary>Raised for shell-level named actions (interrupt, exit, toggle mode, ...).</summary>
    public event EventHandler<UiAction>? ActionRequested;

    /// <summary>The multiline composer that owns draft/caret/history state.</summary>
    internal ComposerView Composer { get; }

    /// <summary>Controller seam for recalling pending steering from the composer Up-key precedence rule.</summary>
    internal Func<string?>? RecallPendingSteering
    {
        set => this.Composer.RecallPendingSteering = value;
    }

    /// <summary>
    /// The borderless chrome that frames the composer region: a subtle dark background, full-width
    /// half-block edges above and below the composer content rows, and, when ready, the <c>&gt;</c> prompt
    /// glyph on the first content row. During startup it stays blank and dark; the operational status row
    /// owns the <c>Initializing…</c> message. Non-focusable and owned here; concrete shells position it
    /// around the composer in <see cref="BuildLayout"/>.
    /// </summary>
    internal ComposerChromeView Chrome { get; }

    /// <summary>
    /// The always-visible one-row operational status pinned directly above the composer. It owns the
    /// spinner/timer lifecycle and is themed per <see cref="OperationalTone"/>; concrete shells position it
    /// between the transcript and the composer in <see cref="BuildLayout"/>.
    /// </summary>
    internal OperationalStatusView Operational { get; }

    /// <summary>The one-line stable-metadata label pinned to the shell's final row.</summary>
    internal Label Status { get; }

    /// <summary>The Warm Ember theme shared by every view this shell constructs.</summary>
    protected TuiTheme Theme { get; }

    /// <summary>The clock used for the deterministic interrupt/exit chord windows.</summary>
    protected TimeProvider TimeSource { get; }

    /// <summary>
    /// The concrete virtualized transcript this shell hosts. Exposed so the shared base can route text
    /// selection, clipboard copy, and Esc-clear through the shell's transcript without a Terminal.Gui type
    /// leaking into the host-neutral state model.
    /// </summary>
    protected abstract VirtualizedTranscriptView TranscriptView { get; }

    /// <summary>The keyboard-only prompt surface, hidden until a prompt is pending.</summary>
    internal PromptOverlay PromptOverlay { get; }

    /// <summary>The hosted <c>/tasks</c> browser overlay, or null when no provider was wired.</summary>
    internal TaskBrowserOverlay? TaskOverlay => this.taskOverlay;

    /// <summary>The browser's headless controller (test/diagnostic seam), or null when no provider was wired.</summary>
    internal TaskBrowserController? TaskController => this.taskController;

    /// <summary>
    /// The slash-command completion menu, owned here and synchronized from the composer. Concrete shells
    /// position it (via <see cref="PlaceCompletion"/>) and add it to their view tree; it stays hidden with
    /// height 0 whenever the composer offers no visible suggestions.
    /// </summary>
    internal CommandCompletionView Completion { get; }

    /// <summary>The most recently applied snapshot.</summary>
    internal UiSessionSnapshot Snapshot { get; private set; }

    /// <summary>
    /// Why this shell stopped, set by the controller just before it requests the application loop to
    /// stop. The mode runner reads it after the loop returns to decide between exit and mode switch.
    /// Null while the shell is running normally.
    /// </summary>
    public TuiShellExit? RequestedExit { get; private set; }

    /// <summary>Number of times the status text actually changed; exposed for tests only.</summary>
    internal int StatusUpdateCount => this.statusUpdateCount;

    /// <summary>The owning application, used by concrete shells for on-thread draw/commit work.</summary>
    protected IApplication HostApp => this.app;

    /// <summary>
    /// Whether a shell-local override currently owns the operational status row, suppressing the projected
    /// status. True while an interrupt/exit chord hint is armed or a transient shell-driven message is
    /// pinned, so a snapshot apply never stomps the chord/transient message.
    /// </summary>
    protected bool HasOperationalOverride =>
        this.transientOperationalOverride is not null || this.chords.CurrentHint is not null;

    /// <summary>Exports the composer state so it survives a shell/mode switch.</summary>
    public ComposerState ExportComposerState() => this.controller.Export();

    /// <summary>
    /// Record why the shell is stopping and ask the owning application loop to stop. Called by the
    /// controller on the UI thread in response to an exit or mode-switch action; the mode runner then
    /// reads <see cref="RequestedExit"/> once the loop returns.
    /// </summary>
    public void RequestStop(TuiShellExit outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        this.ResetChordOverride();
        this.ClearTransientOperationalOverride();
        this.RequestedExit = outcome;
        this.app.RequestStop();
    }

    /// <summary>Restores composer state captured by <see cref="ExportComposerState"/>.</summary>
    public void RestoreComposerState(ComposerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        this.controller.Restore(state);

        // Mirror the draft/caret into the editor first (this resets the preferred column and may retrigger a
        // layout pass), then re-apply the transferred viewport so the restored scroll row and preferred
        // column survive the round trip.
        this.Composer.SetDraft(state.Draft, state.CursorIndex);
        this.controller.UpdateViewport(state.ScrollRow);
        this.controller.MoveCursorTo(state.CursorIndex, state.PreferredDisplayColumn);

        // Ask for a fresh layout pass so the composer regrows to the restored draft, then focus the editor
        // unless startup is active or a modal prompt owns input.
        this.SetNeedsLayout();
        if (this.Snapshot.ActiveOperation?.Kind != StartupOperationKind
            && this.Snapshot.PendingPrompt is null
            && !this.PromptOverlay.Visible
            && this.Composer.CanFocus)
        {
            this.Composer.SetFocus();
        }
    }

    /// <inheritdoc />
    public ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        if (this.IsOnUiThread())
        {
            this.Apply(snapshot);
            return ValueTask.CompletedTask;
        }

        // Off the UI thread: hop onto it via App.Invoke. The completion source uses asynchronous
        // continuations so awaiting callers never resume inline on the UI thread and deadlock it. A
        // cancellation registration releases the awaiter even if the UI loop is not pumping (so the
        // queued callback would otherwise never run), and any synchronous App.Invoke failure (for
        // example a NotInitializedException on a disposed app) is surfaced through the returned task
        // rather than thrown at the caller.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        completion.Task.ContinueWith(
            static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
            registration,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            this.app.Invoke(() =>
            {
                // Skip the mutation if the awaiter was already released (cancelled) or the token
                // fired between queueing and running, so a cancelled snapshot is never applied.
                if (completion.Task.IsCompleted || cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    this.Apply(snapshot);
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return new ValueTask(completion.Task);
    }

    /// <summary>Lays out the composer, status, prompt overlay, and any shell-specific chrome.</summary>
    protected abstract void BuildLayout();

    /// <summary>
    /// Subscribes the shell to a transcript's unhandled printable keys so typing while the transcript has
    /// focus redirects into the composer. Concrete shells call this once the transcript is constructed.
    /// </summary>
    protected void BindTranscriptInput(VirtualizedTranscriptView transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        transcript.UnhandledKeyDown += this.HandleUnhandledShellKey;
        transcript.CopyRequested += this.HandleTranscriptCopyRequested;
    }

    /// <summary>Unsubscribes a transcript previously bound with <see cref="BindTranscriptInput"/>.</summary>
    protected void UnbindTranscriptInput(VirtualizedTranscriptView transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        transcript.UnhandledKeyDown -= this.HandleUnhandledShellKey;
        transcript.CopyRequested -= this.HandleTranscriptCopyRequested;
    }

    /// <summary>Reconciles transcript presentation between two applied snapshots.</summary>
    protected abstract void ApplyTranscriptChanges(UiSessionSnapshot previous, UiSessionSnapshot next);

    /// <summary>
    /// Positions the completion menu for the current suggestion count. <paramref name="height"/> is the
    /// number of option rows to show (0 when hidden). Both retained shells overlay it above the fixed
    /// composer without moving the composer or status.
    /// </summary>
    protected abstract void PlaceCompletion(int height, bool visible);

    /// <summary>
    /// Reacts to a composer content/caret change by remeasuring the composer's height and re-applying its
    /// internal scroll. Concrete shells recalculate their bottom-anchored geometry here.
    /// </summary>
    protected abstract void OnComposerLayoutInvalidated();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.disposed)
        {
            this.disposed = true;

            // Tear down both shell-owned status timers first: ResetChordOverride removes the chord expiry
            // timeout (and re-projects the operational row, which re-arms the spinner for any still-active
            // work), then ClearTransientOperationalOverride removes a pinned transient timeout. base.Dispose
            // below disposes the child OperationalStatusView, whose own Dispose removes that spinner timeout,
            // so no status timer survives the shell.
            this.ResetChordOverride();
            this.ClearTransientOperationalOverride();
            this.CancelScheduledComposerLayoutRecalc();
            this.Composer.ShellKeyHandler = null;
            this.Composer.Submitted -= this.OnComposerSubmitted;
            this.Composer.ActionRequested -= this.OnComposerActionRequested;
            this.Composer.PointerActionRequested -= this.OnComposerPointerActionRequested;
            this.Composer.CompletionChanged -= this.OnCompletionChanged;
            this.Composer.LayoutInvalidated -= this.OnComposerLayoutInvalidatedHandler;
            this.Initialized -= this.OnShellInitialized;

            // Hide() cancels the pump, unsubscribes Changed, releases any pause lease (resuming the main
            // agent), and closes the controller — so a mode switch or shutdown never leaves the agent paused.
            // The overlay View itself is disposed by base.Dispose below.
            this.taskOverlay?.Hide();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Focus the composer once the shell is initialized so an initially ready shell accepts typing without
    /// a first keystroke to move focus, unless startup is active or a modal prompt owns input.
    /// </summary>
    private void OnShellInitialized(object? sender, EventArgs args)
    {
        if (!this.composerDisabled &&
            this.Snapshot.PendingPrompt is null &&
            !this.PromptOverlay.Visible)
        {
            this.Composer.SetFocus();
        }
    }

    /// <summary>
    /// Routes a key the transcript did not consume: the interrupt/exit chords win first (so Esc/Ctrl+C work
    /// with the transcript focused), then a printable, non-modifier key is redirected into the composer,
    /// focusing it so typing anywhere edits the draft. A pending modal prompt is never redirected.
    /// </summary>
    private bool HandleUnhandledShellKey(Key key)
    {
        if (this.PromptOverlay.Visible)
        {
            return false;
        }

        if (this.TryHandleShellKey(key))
        {
            return true;
        }

        if (!TryGetPrintable(key, out var text))
        {
            return false;
        }

        this.Composer.SetFocus();
        this.Composer.InsertFromShell(text);
        return true;
    }

    /// <summary>
    /// Arbitrates the shell-owned Esc/Ctrl+C keys before the composer's own mapping. Esc is a local
    /// dismiss/cancel key only: it dismisses an open completion, clears a transcript selection, clears a
    /// transient operational override, or cancels an armed chord — and is otherwise consumed as a no-op so
    /// it can never bubble to Terminal.Gui's default Esc quit binding or arm a global interrupt. Ctrl+C
    /// first copies a composer selection, then a transcript selection, then arms/fires the explicit exit
    /// chord. Returns true when the key was consumed here so no printable/action routing runs for it.
    /// </summary>
    private bool TryHandleShellKey(Key key)
    {
        if (this.HasVisibleModalOverlay())
        {
            // While the browser is up it owns the keyboard (it holds focus); the shell chords stand down so
            // Ctrl+B is consumed by the overlay, never re-interpreted as the background chord below.
            return false;
        }

        if (key == Key.Esc)
        {
            if (this.Completion.Visible)
            {
                this.Composer.DismissCompletion();
                return true;
            }

            if (this.TryClearTranscriptSelection())
            {
                return true;
            }

            if (this.TryClearTransientOperationalOverride())
            {
                return true;
            }

            // Cancel any armed chord (e.g. a pending Ctrl+C exit) so no stale "Press … again" hint or chord
            // state survives an Esc — even the Esc that just dismissed an overlay.
            if (this.chords.ArmedAction != ShellChordAction.None)
            {
                this.ResetChordOverride();
                return true;
            }

            // Escape is a local dismiss/cancel key only. With nothing left to dismiss it is still fully
            // consumed (returns true) so it can never fall through to Terminal.Gui's default Esc quit
            // binding and close the application, and it never arms or fires a global interrupt chord —
            // interrupting/terminating the session stays on the explicit Ctrl+C chord.
            return true;
        }

        if (key == Key.C.WithCtrl)
        {
            if (this.TryCopyComposerSelection())
            {
                return true;
            }

            if (this.TryCopyTranscriptSelection())
            {
                return true;
            }

            return this.ApplyChord(this.chords.HandleCtrlC());
        }

        if (this.TryHandleTranscriptNavigationKey(key))
        {
            return true;
        }

        if (this.taskController is not null && key == Key.B.WithCtrl)
        {
            // Ctrl+B is an output/shell chord, never a browser opener (that is /tasks): the controller
            // releases an active UI attachment, otherwise it sends the selected — or latest — running
            // foreground shell to the background and returns the real TryDetach outcome to surface. It never
            // interrupts the main agent.
            var message = this.taskController.HandleBackgroundChord();
            this.ShowTransientOperationalStatus(
                new OperationalStatus(message, OperationalTone.Ready, false),
                TimeSpan.FromSeconds(2));
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    protected override bool OnKeyDown(Key key)
    {
        if (this.HasVisibleModalOverlay())
        {
            return false;
        }

        return this.TryHandleTranscriptNavigationKey(key) || base.OnKeyDown(key);
    }

    private bool HasVisibleModalOverlay() =>
        this.PromptOverlay.Visible || this.taskOverlay?.Visible == true;

    private bool TryHandleTranscriptNavigationKey(Key key)
    {
        if (this.HasVisibleModalOverlay() || key != Key.End.WithCtrl)
        {
            return false;
        }

        this.TranscriptView.JumpToNewest();
        return true;
    }

    /// <summary>
    /// Clears an active transcript selection when the first Esc arrives, before any chord arming, and
    /// restores the projected operational status. Returns true when a selection was actually cleared so the
    /// Esc is consumed here and never falls through to interrupt-chord arming.
    /// </summary>
    private bool TryClearTranscriptSelection()
    {
        if (!this.TranscriptView.HasSelection)
        {
            return false;
        }

        this.TranscriptView.ClearSelection();
        this.RestoreProjectedOperationalStatus();
        return true;
    }

    /// <summary>
    /// Copies an active transcript selection to the clipboard when Ctrl+C arrives, before any exit-chord
    /// arming. Returns true whenever a selection was present, so the exit chord never arms while text is
    /// selected; the copy itself (success status or the clipboard-unavailable fallback) is handled by
    /// <see cref="CopyTranscriptSelection"/>.
    /// </summary>
    private bool TryCopyTranscriptSelection()
    {
        if (!this.TranscriptView.HasSelection)
        {
            return false;
        }

        this.CopyTranscriptSelection();
        return true;
    }

    /// <summary>
    /// Copies an active composer selection to the clipboard when Ctrl+C arrives, taking precedence over any
    /// transcript selection and before any exit-chord arming. Returns true whenever a selection was present,
    /// so the exit chord never arms while composer text is selected; the copy itself (success status or the
    /// clipboard-unavailable fallback) is handled by <see cref="CopyComposerSelection"/>.
    /// </summary>
    private bool TryCopyComposerSelection()
    {
        if (!this.Composer.HasComposerSelection)
        {
            return false;
        }

        this.CopyComposerSelection(this.Composer.SelectedComposerText);
        return true;
    }

    /// <summary>
    /// The default <c>clipboardReader</c> seam: reads the running application's clipboard once. A missing
    /// clipboard backend or a driver that refuses the read is reported as <see cref="ClipboardReadResult.Available"/>
    /// false (never an exception), so a pointer paste surfaces a deterministic "Clipboard unavailable" warning.
    /// A successful read returns the retrieved text, which may legitimately be empty.
    /// </summary>
    private ClipboardReadResult ReadApplicationClipboard() =>
        this.app.Clipboard?.TryGetClipboardData(out var text) == true
            ? new ClipboardReadResult(true, text)
            : new ClipboardReadResult(false, string.Empty);

    /// <summary>
    /// Routes the composer's semantic pointer gestures through a single guard: while a modal prompt is up,
    /// the composer is startup-disabled, or it is not accepting input, every pointer action is ignored so a
    /// pointer can never copy, paste, or open a menu behind an overlay or before the editor is live. A
    /// <see cref="ComposerPointerActionKind.CopySelection"/> copies the reported selection through the same
    /// shell path as Ctrl+C; <see cref="ComposerPointerActionKind.PasteClipboard"/> pastes the clipboard at
    /// the caret the composer already positioned; <see cref="ComposerPointerActionKind.ShowContextMenu"/>
    /// makes the composer's context menu visible at the pointer.
    /// </summary>
    private void OnComposerPointerActionRequested(object? sender, ComposerPointerActionRequestedEventArgs e)
    {
        if (this.PromptOverlay.Visible || this.composerDisabled || !this.Composer.InputEnabled)
        {
            return;
        }

        switch (e.Kind)
        {
            case ComposerPointerActionKind.CopySelection:
                this.CopyComposerSelection(e.SelectedText ?? string.Empty);
                break;
            case ComposerPointerActionKind.PasteClipboard:
                this.PasteComposerClipboard();
                break;
            case ComposerPointerActionKind.ShowContextMenu:
                this.ShowComposerContextMenu(e.ScreenPosition);
                break;
        }
    }

    /// <summary>
    /// Pastes the clipboard into the composer at the caret the pointer gesture already positioned. The
    /// clipboard is read exactly once through the <c>clipboardReader</c> seam: an unavailable read leaves the
    /// draft and caret untouched and pins a transient "Clipboard unavailable" Warning; an available but empty
    /// read likewise makes no mutation and pins "Clipboard is empty"; a non-empty read is injected as a native
    /// bracketed paste (incremental insertion at the caret — the draft text and caret are never assigned
    /// directly) and a transient "{N} symbol(s) pasted from clipboard" Ready status is pinned for 1.5 seconds.
    /// </summary>
    private void PasteComposerClipboard()
    {
        var result = this.clipboardReader();
        if (!result.Available)
        {
            this.ShowTransientOperationalStatus(
                new OperationalStatus("Clipboard unavailable", OperationalTone.Warning, false),
                TimeSpan.FromSeconds(1.5));
            return;
        }

        if (string.IsNullOrEmpty(result.Text))
        {
            this.ShowTransientOperationalStatus(
                new OperationalStatus("Clipboard is empty", OperationalTone.Warning, false),
                TimeSpan.FromSeconds(1.5));
            return;
        }

        this.Composer.NewPasteEvent(result.Text);
        this.ShowTransientOperationalStatus(
            new OperationalStatus(ClipboardStatusText.Pasted(result.Text), OperationalTone.Ready, false),
            TimeSpan.FromSeconds(1.5));
    }

    /// <summary>
    /// Makes the composer's existing context menu visible at the pointer's screen position. When the composer
    /// exposes no context menu the gesture is reported with a transient "Context menu unavailable" Warning
    /// rather than silently doing nothing.
    /// </summary>
    private void ShowComposerContextMenu(System.Drawing.Point screenPosition)
    {
        var menu = this.Composer.ContextMenu;
        if (menu is null)
        {
            this.ShowTransientOperationalStatus(
                new OperationalStatus("Context menu unavailable", OperationalTone.Warning, false),
                TimeSpan.FromSeconds(1.5));
            return;
        }

        menu.MakeVisible(screenPosition);
    }

    /// <summary>
    /// Copies the active composer selection to the clipboard. When the selection contains zero copyable symbols
    /// the selection is cleared with a deterministic "0 symbols copied to clipboard" confirmation without
    /// touching the clipboard writer. Otherwise, on a successful write the selection highlight is cleared and a
    /// transient "{N} symbol(s) copied to clipboard" status is pinned for 1.5 seconds; when the clipboard is
    /// unavailable the selection is preserved and a transient "Clipboard unavailable" Warning is pinned instead.
    /// The draft text and caret are never mutated.
    /// </summary>
    private void CopyComposerSelection(string text)
    {
        if (ClipboardStatusText.CountSymbols(text) == 0)
        {
            this.Composer.ClearComposerSelection();
            this.ShowTransientOperationalStatus(
                new OperationalStatus(ClipboardStatusText.Copied(text), OperationalTone.Ready, false),
                TimeSpan.FromSeconds(1.5));
            return;
        }

        if (this.clipboardWriter(text))
        {
            this.Composer.ClearComposerSelection();
            this.ShowTransientOperationalStatus(
                new OperationalStatus(ClipboardStatusText.Copied(text), OperationalTone.Ready, false),
                TimeSpan.FromSeconds(1.5));
            return;
        }

        this.ShowTransientOperationalStatus(
            new OperationalStatus("Clipboard unavailable", OperationalTone.Warning, false),
            TimeSpan.FromSeconds(1.5));
    }

    /// <summary>
    /// Handles a transcript copy request (a fresh left click on an active selection) by routing through the
    /// same copy path as Ctrl+C.
    /// </summary>
    private void HandleTranscriptCopyRequested() => this.CopyTranscriptSelection();

    /// <summary>
    /// Copies the active transcript selection to the clipboard. On success the selection is cleared and a
    /// transient "{N} symbol(s) copied to clipboard" status is pinned for 1.5 seconds before the projected
    /// status is restored; when the clipboard is unavailable the selection is preserved and a transient
    /// "Clipboard unavailable" Warning is pinned instead. No-op when nothing is selected.
    /// </summary>
    private void CopyTranscriptSelection()
    {
        if (!this.TranscriptView.HasSelection)
        {
            return;
        }

        var text = this.TranscriptView.GetSelectedText();

        // An empty or newline-only selection has no symbols to copy. Clear it and report a deterministic
        // "0 symbols copied to clipboard" confirmation instead of routing through the clipboard writer, whose
        // skipped/failed write would otherwise surface a misleading "Clipboard unavailable" warning.
        if (ClipboardStatusText.CountSymbols(text) == 0)
        {
            this.TranscriptView.ClearSelection();
            this.ShowTransientOperationalStatus(
                new OperationalStatus(ClipboardStatusText.Copied(text), OperationalTone.Ready, false),
                TimeSpan.FromSeconds(1.5));
            return;
        }

        if (this.clipboardWriter(text))
        {
            this.TranscriptView.ClearSelection();
            this.ShowTransientOperationalStatus(
                new OperationalStatus(ClipboardStatusText.Copied(text), OperationalTone.Ready, false),
                TimeSpan.FromSeconds(1.5));
            return;
        }

        this.ShowTransientOperationalStatus(
            new OperationalStatus("Clipboard unavailable", OperationalTone.Warning, false),
            TimeSpan.FromSeconds(1.5));
    }

    /// <summary>
    /// Pins a shell-driven transient status onto the operational row and schedules a one-shot timeout that
    /// clears it and restores the projected status. Any previous transient override (and its timer) is torn
    /// down first, and the expiry callback is inert once the shell is disposed.
    /// </summary>
    private void ShowTransientOperationalStatus(OperationalStatus status, TimeSpan duration)
    {
        this.ClearTransientOperationalOverride();
        this.transientOperationalOverride = status;
        this.Operational.SetStatus(status);
        this.transientOperationalTimeout = this.addTimeout(
            duration,
            () =>
            {
                this.transientOperationalTimeout = null;
                if (this.disposed)
                {
                    return false;
                }

                this.transientOperationalOverride = null;
                this.RestoreProjectedOperationalStatus();
                return false;
            });
    }

    /// <summary>
    /// Whether there is anything an interrupt chord should be allowed to interrupt: the injected active-work
    /// delegate is true, an operation is active, background tasks are running, or an incomplete tool exists.
    /// </summary>
    private bool HasInterruptibleWork()
    {
        if (this.hasActiveWork() ||
            this.Snapshot.ActiveOperation is not null ||
            this.Snapshot.RunningTasks > 0)
        {
            return true;
        }

        return this.Snapshot.Transcript.Any(
            block => block is ToolTranscriptBlock { Complete: false });
    }

    /// <summary>
    /// Applies a chord result: an unconsumed result lets the key fall through; an arming result pins the
    /// warning hint and schedules a one-shot expiry timeout; a firing result restores the projected status
    /// and raises the interrupt/exit action.
    /// </summary>
    private bool ApplyChord(ShellChordResult result)
    {
        if (!result.Consumed)
        {
            // The chord state declined the key (e.g. Esc after the interruptible work vanished) and reset
            // itself in the process. Tear down the now-orphaned expiry timeout and restore the projected
            // status so a stale "Press Esc again" hint never lingers into an idle frame. The re-projection
            // re-derives from any pinned transient override first, so an unrelated override is preserved.
            if (this.chords.ArmedAction == ShellChordAction.None)
            {
                this.StopChordTimeout();
                this.RestoreProjectedOperationalStatus();
            }

            return false;
        }

        this.StopChordTimeout();
        if (result.Hint is { } hint)
        {
            this.Operational.SetStatus(hint);
            var window = this.chords.ArmedAction == ShellChordAction.Interrupt
                ? ShellCommandChordState.InterruptWindow
                : ShellCommandChordState.ExitWindow;
            this.chordTimeout = this.addTimeout(
                window + TimeSpan.FromMilliseconds(1),
                () =>
                {
                    this.chordTimeout = null;
                    if (this.disposed)
                    {
                        return false;
                    }

                    this.chords.Expire();
                    this.RestoreProjectedOperationalStatus();
                    return false;
                });
            return true;
        }

        this.RestoreProjectedOperationalStatus();
        if (result.Action == ShellChordAction.Interrupt)
        {
            this.ActionRequested?.Invoke(this, UiAction.Interrupt);
        }
        else if (result.Action == ShellChordAction.Exit)
        {
            this.ActionRequested?.Invoke(this, UiAction.Exit);
        }

        return true;
    }

    /// <summary>Cancels any armed chord, tears down its expiry timeout, and restores the projected status.</summary>
    private void ResetChordOverride()
    {
        this.StopChordTimeout();
        this.chords.Reset();
        this.RestoreProjectedOperationalStatus();
    }

    private void StopChordTimeout()
    {
        if (this.chordTimeout is not { } token)
        {
            return;
        }

        this.chordTimeout = null;
        this.removeTimeout(token);
    }

    private bool TryClearTransientOperationalOverride()
    {
        if (this.transientOperationalOverride is null)
        {
            return false;
        }

        this.ClearTransientOperationalOverride();
        this.RestoreProjectedOperationalStatus();
        return true;
    }

    private void ClearTransientOperationalOverride()
    {
        this.transientOperationalOverride = null;
        if (this.transientOperationalTimeout is not { } token)
        {
            return;
        }

        this.transientOperationalTimeout = null;
        this.removeTimeout(token);
    }

    /// <summary>
    /// Re-derives the operational row from the highest-priority owner: a pinned transient override, then an
    /// armed chord hint, then the snapshot projection.
    /// </summary>
    private void RestoreProjectedOperationalStatus()
    {
        var status = this.transientOperationalOverride ??
            this.chords.CurrentHint ??
            OperationalStatusProjector.Project(this.Snapshot, this.toolDisplayMode);
        this.Operational.SetStatus(status);
    }

    private static bool TryGetPrintable(Key key, out string text)
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

    private void OnComposerLayoutInvalidatedHandler(object? sender, EventArgs e) =>
        this.ScheduleComposerLayoutRecalc();

    /// <summary>
    /// Coalesces composer layout invalidations: many content/caret/completion signals in a single UI
    /// iteration schedule at most one zero-delay recalc, so a keystroke never triggers several synchronous
    /// re-layouts. The scheduled callback runs once, clears the token, and re-arms only on the next signal.
    /// </summary>
    private void ScheduleComposerLayoutRecalc()
    {
        if (this.disposed || this.composerLayoutTimeout is not null)
        {
            return;
        }

        this.composerLayoutTimeout = this.addTimeout(TimeSpan.Zero, () =>
        {
            this.composerLayoutTimeout = null;
            if (!this.disposed)
            {
                this.OnComposerLayoutInvalidated();
            }

            return false;
        });
    }

    /// <summary>Removes any pending coalesced composer recalc so no timer survives the shell.</summary>
    private void CancelScheduledComposerLayoutRecalc()
    {
        if (this.composerLayoutTimeout is { } timeout)
        {
            this.removeTimeout(timeout);
            this.composerLayoutTimeout = null;
        }
    }

    private void Apply(UiSessionSnapshot snapshot)
    {
        var previous = this.Snapshot;
        this.Snapshot = snapshot;

        // A completed turn (or otherwise vanished work) must disarm an interrupt chord that now has nothing
        // to interrupt, so the stale "Press Esc again" hint never survives into an idle frame.
        if (this.chords.ArmedAction == ShellChordAction.Interrupt &&
            !this.HasInterruptibleWork())
        {
            this.ResetChordOverride();
        }

        this.UpdateMetadata(snapshot);
        this.UpdateProjectedOperationalStatus(snapshot);
        this.UpdateComposerAvailability(snapshot);
        this.UpdatePrompt(snapshot);
        this.ApplyTranscriptChanges(previous, snapshot);
    }

    private bool IsOnUiThread() =>
        this.app.MainThreadId is { } mainThreadId && mainThreadId == Environment.CurrentManagedThreadId;

    private void UpdateMetadata(UiSessionSnapshot snapshot)
    {
        var width = this.Status.Frame.Width;
        if (width <= 0)
        {
            width = this.Frame.Width > 0 ? this.Frame.Width : DefaultStatusWidth;
        }

        var text = this.statusProjection(snapshot, width);
        if (string.Equals(text, this.statusText, StringComparison.Ordinal))
        {
            return;
        }

        this.statusText = text;
        this.Status.Text = text;
        this.statusUpdateCount++;
    }

    /// <summary>
    /// Projects the semantic snapshot onto the operational status row unless a shell-local override is
    /// active, driving the spinner/timer lifecycle owned by <see cref="OperationalStatusView"/>.
    /// </summary>
    private void UpdateProjectedOperationalStatus(UiSessionSnapshot snapshot)
    {
        if (this.HasOperationalOverride)
        {
            return;
        }

        this.Operational.SetStatus(OperationalStatusProjector.Project(snapshot, this.toolDisplayMode));
    }

    /// <summary>
    /// Reconciles composer readiness from the semantic snapshot before prompt focus is settled. While the
    /// startup operation is active the real editor is hidden and disabled and the chrome stays blank and
    /// dark — the operational status row owns the <c>Initializing…</c> message — so it is visually impossible
    /// to type. When startup clears, the editor is shown
    /// and re-enabled, the chrome shows <c>&gt;</c>, and — only on the transition back to ready and only when
    /// no prompt is pending — the composer regains focus, so an open prompt overlay is never robbed of it.
    /// </summary>
    private void UpdateComposerAvailability(UiSessionSnapshot snapshot)
    {
        var startingUp = snapshot.ActiveOperation?.Kind == StartupOperationKind;
        if (startingUp || this.composerLockedByAttachment)
        {
            this.Chrome.SetReady(false);
            this.Composer.InputEnabled = false;
            this.Composer.CanFocus = false;
            this.Composer.Visible = false;
            this.Completion.Visible = false;
            this.composerDisabled = true;
            return;
        }

        this.Chrome.SetReady(true);
        this.Composer.InputEnabled = true;
        this.Composer.CanFocus = true;
        this.Composer.Visible = true;

        if (!this.composerDisabled)
        {
            // Already ready on the previous frame: completion visibility is managed by the composer's
            // own CompletionChanged events, so avoid re-syncing (and re-laying-out) on every snapshot.
            return;
        }

        this.composerDisabled = false;

        // Returning from the disabled state: re-evaluate the completion overlay now that suggestions are
        // relevant again (e.g. a restored slash draft), which also restores the visibility that startup
        // forced hidden.
        this.SyncCompletion();

        // Focus the editor on the transition back to ready, but never when a prompt is pending or the task
        // browser is open: the modal prompt overlay stays topmost and keeps focus, and an open browser owns
        // the keyboard — a composer unlock (e.g. an attachment auto-releasing on shell completion) must not
        // steal focus out from under either.
        if (snapshot.PendingPrompt is null && !this.PromptOverlay.Visible && this.taskOverlay?.Visible != true)
        {
            this.Composer.SetFocus();
        }
    }

    private void UpdatePrompt(UiSessionSnapshot snapshot)
    {
        if (snapshot.PendingPrompt is { } prompt)
        {
            // A modal prompt takes over input, so any armed interrupt/exit chord is abandoned rather than
            // left pinned behind the overlay.
            this.ResetChordOverride();
            this.PromptOverlay.Update(prompt);
            this.PromptOverlay.SetFocus();
            return;
        }

        if (!this.PromptOverlay.Visible)
        {
            return;
        }

        this.PromptOverlay.Update(null);
        if (this.taskOverlay?.Visible == true)
        {
            // The browser was open behind the prompt: return focus to it, not the composer.
            this.taskOverlay.SetFocus();
        }
        else if (!this.composerDisabled)
        {
            this.Composer.SetFocus();
        }
    }

    private void OpenTaskBrowser()
    {
        // Show() owns controller.Open() + a fresh pump and is idempotent while already active, so a repeated
        // /tasks never re-Opens (which would rebind the subscription and dispose the live pump under it).
        // Re-invoking the provider inside the first Show picks up the live TaskManager even though the overlay
        // was built once; before the first turn the provider returns null and the browser opens empty.
        this.taskOverlay?.Show();
    }

    private void SetComposerAttachmentLock(bool locked)
    {
        if (this.composerLockedByAttachment == locked)
        {
            return;
        }

        this.composerLockedByAttachment = locked;
        this.UpdateComposerAvailability(this.Snapshot);
    }

    /// <summary>
    /// Marshaled browser-change callback (the overlay hops <see cref="TaskBrowserController.Changed"/> onto
    /// the UI thread, and <c>Hide()</c> invokes it synchronously on the UI thread): folds the attachment
    /// pause lease into composer availability and, when the browser has just closed, restores focus — the
    /// permission prompt wins, otherwise the composer if it is available and not attachment-locked.
    /// </summary>
    private void OnTaskBrowserChanged()
    {
        if (this.taskOverlay is null || this.disposed)
        {
            return;
        }

        this.SetComposerAttachmentLock(this.taskOverlay.IsComposerLocked);

        if (this.taskOverlay.Visible)
        {
            // Still open: it must keep the keyboard. A composer unlock during an auto-release
            // (Complete/Fail/Stop) must never steal focus, so reclaim it defensively — but a permission
            // prompt (topmost) still wins.
            if (this.PromptOverlay.Visible)
            {
                this.PromptOverlay.SetFocus();
            }
            else
            {
                this.taskOverlay.SetFocus();
            }

            return;
        }

        if (this.PromptOverlay.Visible)
        {
            this.PromptOverlay.SetFocus();
        }
        else if (!this.composerDisabled && !this.composerLockedByAttachment)
        {
            this.Composer.SetFocus();
        }
    }

    private void OnComposerSubmitted(object? sender, string text)
    {
        // An exact `/tasks` submission is intercepted locally BEFORE PromptSubmitted fires (which is what
        // feeds TuiController.OnSubmitted's dispatch guard), so it opens the browser even while the agent is
        // busy and never dispatches a turn. The composer already cleared the draft/completion on submit.
        if (this.taskOverlay is not null && TaskBrowserController.IsOpenRequest(text))
        {
            this.OpenTaskBrowser();
            return;
        }

        // Explicit submits (prompts, slash commands, bash) always resume auto-following: jump the transcript
        // back to the newest row before forwarding so the response the user just asked for is visible even
        // when they had scrolled up. Background output alone never forces this — only an explicit submit.
        this.TranscriptView.JumpToNewest();
        this.PromptSubmitted?.Invoke(this, text);
    }

    private void OnComposerActionRequested(object? sender, UiAction action)
    {
        // Switching mode abandons any armed chord so a half-typed interrupt/exit never survives the switch.
        if (action == UiAction.ToggleMode)
        {
            this.ResetChordOverride();
        }

        this.ActionRequested?.Invoke(this, action);
    }

    private void OnCompletionChanged(object? sender, EventArgs e) => this.SyncCompletion();

    /// <summary>
    /// Mirrors the composer's current suggestions/selection into the completion menu, asks the concrete
    /// shell to position it, and toggles its visibility. Kept hidden with height 0 when there is nothing to
    /// show so it never affects layout while inactive.
    /// </summary>
    private void SyncCompletion()
    {
        this.Completion.SetSuggestions(this.Composer.Suggestions, this.Composer.SelectedSuggestionIndex);
        var height = this.Completion.DesiredHeight;
        var visible = height > 0;
        this.PlaceCompletion(height, visible);
        this.Completion.Visible = visible;
        this.StripAutocompletePopup();
        this.SetNeedsLayout();
    }

    /// <summary>
    /// Removes any sub-view the base <see cref="TextView"/> autocomplete appended to this shell. The
    /// composer suppresses word suggestions (see <see cref="ComposerView"/>), but Terminal.Gui still
    /// parents the autocomplete popup to the running top-level on the first edit, which would append after
    /// the <see cref="PromptOverlay"/> and break the fixed insertion order. Stripping it keeps the prompt
    /// overlay topmost without reordering (which desynchronizes Terminal.Gui's disposal bookkeeping).
    /// </summary>
    private void StripAutocompletePopup()
    {
        foreach (var view in this.SubViews.Where(v => !this.ownedSubViews.Contains(v)).ToList())
        {
            this.Remove(view);
        }
    }
}
