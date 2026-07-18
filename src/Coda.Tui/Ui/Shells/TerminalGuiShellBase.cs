using System.Text;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

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
    private readonly Func<bool> hasActiveWork;
    private readonly ShellCommandChordState chords;
    private readonly Func<TimeSpan, Func<bool>, object> addTimeout;
    private readonly Func<object, bool> removeTimeout;
    private readonly Func<string, bool> clipboardWriter;
    private object? chordTimeout;
    private object? transientOperationalTimeout;
    private object? composerLayoutTimeout;
    private OperationalStatus? transientOperationalOverride;

    private string? statusText;
    private int statusUpdateCount;
    private bool composerDisabled;
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
        Func<TimeSpan, Func<bool>, object>? addTimeout = null,
        Func<object, bool>? removeTimeout = null,
        TuiTheme? theme = null,
        Func<UiSessionSnapshot, int, string>? statusProjection = null)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.Snapshot = initialSnapshot ?? throw new ArgumentNullException(nameof(initialSnapshot));
        this.statusProjection = statusProjection ?? StatusProjector.Project;
        this.hasActiveWork = hasActiveWork ?? (() => false);
        this.TimeSource = timeProvider ?? TimeProvider.System;
        this.clipboardWriter = clipboardWriter ??
            (text => this.app.Clipboard?.TrySetClipboardData(text) == true);
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

        // The composer routes every key through the shell first so the interrupt/exit chords win over the
        // composer's own printable/action mapping regardless of which view currently holds focus.
        this.Composer.ShellKeyHandler = this.TryHandleShellKey;

        this.Composer.Submitted += this.OnComposerSubmitted;
        this.Composer.ActionRequested += this.OnComposerActionRequested;
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
            this.Composer.CompletionChanged -= this.OnCompletionChanged;
            this.Composer.LayoutInvalidated -= this.OnComposerLayoutInvalidatedHandler;
            this.Initialized -= this.OnShellInitialized;
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
    /// Arbitrates the shell-owned Esc/Ctrl+C chords before the composer's own mapping. Esc first dismisses
    /// an open completion, then clears a transcript selection (Task 12 seam), then clears a transient
    /// operational override, then cancels an armed exit chord, and only then arms/fires the interrupt chord.
    /// Ctrl+C first copies a transcript selection (Task 12 seam), then arms/fires the exit chord. Returns
    /// true when the key was consumed here so no printable/action routing runs for it.
    /// </summary>
    private bool TryHandleShellKey(Key key)
    {
        if (this.PromptOverlay.Visible)
        {
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

            if (this.chords.ArmedAction == ShellChordAction.Exit)
            {
                this.ResetChordOverride();
                return true;
            }

            return this.ApplyChord(this.chords.HandleEscape(this.HasInterruptibleWork()));
        }

        if (key == Key.C.WithCtrl)
        {
            if (this.TryCopyTranscriptSelection())
            {
                return true;
            }

            return this.ApplyChord(this.chords.HandleCtrlC());
        }

        return false;
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
        if (!string.IsNullOrEmpty(text) && this.clipboardWriter(text))
        {
            this.TranscriptView.ClearSelection();
            this.ShowTransientOperationalStatus(
                new OperationalStatus(CopySuccessMessage(text), OperationalTone.Ready, false),
                TimeSpan.FromSeconds(1.5));
            return;
        }

        this.ShowTransientOperationalStatus(
            new OperationalStatus("Clipboard unavailable", OperationalTone.Warning, false),
            TimeSpan.FromSeconds(1.5));
    }

    /// <summary>
    /// The transient confirmation for a successful copy, counting the copied Unicode grapheme/text elements
    /// (combining sequences and emoji count as one each) and excluding CR/LF row separators. Singular for a
    /// single symbol, plural otherwise.
    /// </summary>
    private static string CopySuccessMessage(string text)
    {
        var count = CountSymbols(text);
        return count == 1
            ? "1 symbol copied to clipboard"
            : $"{count} symbols copied to clipboard";
    }

    /// <summary>
    /// Counts the Unicode grapheme/text elements in <paramref name="text"/>, treating combining sequences
    /// and emoji as a single symbol and skipping CR/LF separators introduced by the multi-row selection.
    /// </summary>
    private static int CountSymbols(string text)
    {
        var count = 0;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            if (element is "\r" or "\n" or "\r\n")
            {
                continue;
            }

            count++;
        }

        return count;
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
            OperationalStatusProjector.Project(this.Snapshot);
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

        this.Operational.SetStatus(OperationalStatusProjector.Project(snapshot));
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
        if (startingUp)
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

        // Focus the editor on the transition back to ready, but never when a prompt is pending: the modal
        // overlay stays topmost and keeps focus.
        if (snapshot.PendingPrompt is null && !this.PromptOverlay.Visible)
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
        if (!this.composerDisabled)
        {
            this.Composer.SetFocus();
        }
    }

    private void OnComposerSubmitted(object? sender, string text)
    {
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
