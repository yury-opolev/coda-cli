using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.Input;
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

    private readonly IApplication app;
    private readonly ComposerController controller;
    private readonly IUiEventPublisher publisher;
    private readonly Func<UiSessionSnapshot, int, string> statusProjection;

    private string? statusText;
    private View? focusBeforePrompt;
    private int statusUpdateCount;
    private bool disposed;

    protected TerminalGuiShellBase(
        IApplication app,
        ComposerController controller,
        IUiEventPublisher publisher,
        UiSessionSnapshot initialSnapshot,
        Func<UiSessionSnapshot, int, string>? statusProjection = null)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.Snapshot = initialSnapshot ?? throw new ArgumentNullException(nameof(initialSnapshot));
        this.statusProjection = statusProjection ?? StatusProjector.Project;

        this.Composer = new ComposerView(controller);
        this.Status = new Label { CanFocus = false };
        this.PromptOverlay = new PromptOverlay(publisher);

        this.Composer.Submitted += this.OnComposerSubmitted;
        this.Composer.ActionRequested += this.OnComposerActionRequested;

        this.BuildLayout();
    }

    /// <summary>Raised when the composer submits a prompt; carries the submitted text.</summary>
    public event EventHandler<string>? PromptSubmitted;

    /// <summary>Raised for shell-level named actions (interrupt, exit, toggle mode, ...).</summary>
    public event EventHandler<UiAction>? ActionRequested;

    /// <summary>The multiline composer that owns draft/caret/history state.</summary>
    internal ComposerView Composer { get; }

    /// <summary>The one-line status label pinned below the composer.</summary>
    internal Label Status { get; }

    /// <summary>The keyboard-only prompt surface, hidden until a prompt is pending.</summary>
    internal PromptOverlay PromptOverlay { get; }

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
        this.RequestedExit = outcome;
        this.app.RequestStop();
    }

    /// <summary>Restores composer state captured by <see cref="ExportComposerState"/>.</summary>
    public void RestoreComposerState(ComposerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        this.controller.Restore(state);
        this.Composer.SetDraft(state.Draft, state.CursorIndex);
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

    /// <summary>Reconciles transcript presentation between two applied snapshots.</summary>
    protected abstract void ApplyTranscriptChanges(UiSessionSnapshot previous, UiSessionSnapshot next);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.disposed)
        {
            this.disposed = true;
            this.Composer.Submitted -= this.OnComposerSubmitted;
            this.Composer.ActionRequested -= this.OnComposerActionRequested;
        }

        base.Dispose(disposing);
    }

    private void Apply(UiSessionSnapshot snapshot)
    {
        var previous = this.Snapshot;
        this.Snapshot = snapshot;

        this.UpdateStatus(snapshot);
        this.UpdatePrompt(snapshot);
        this.ApplyTranscriptChanges(previous, snapshot);
    }

    private bool IsOnUiThread() =>
        this.app.MainThreadId is { } mainThreadId && mainThreadId == Environment.CurrentManagedThreadId;

    private void UpdateStatus(UiSessionSnapshot snapshot)
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

    private void UpdatePrompt(UiSessionSnapshot snapshot)
    {
        if (snapshot.PendingPrompt is { } prompt)
        {
            if (!this.PromptOverlay.Visible)
            {
                this.focusBeforePrompt = this.Composer;
            }

            this.PromptOverlay.Update(prompt);
            this.PromptOverlay.SetFocus();
            return;
        }

        if (this.PromptOverlay.Visible)
        {
            this.PromptOverlay.Update(null);
            (this.focusBeforePrompt ?? this.Composer).SetFocus();
            this.focusBeforePrompt = null;
        }
    }

    private void OnComposerSubmitted(object? sender, string text) => this.PromptSubmitted?.Invoke(this, text);

    private void OnComposerActionRequested(object? sender, UiAction action) => this.ActionRequested?.Invoke(this, action);
}
