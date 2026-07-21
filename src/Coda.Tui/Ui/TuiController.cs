using Coda.Tui.Agent;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mode;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Ui;

/// <summary>
/// A host-side handle onto the currently running Terminal.Gui shell. It exposes only the two
/// operations the controller needs across a mode/shell transition: exporting the composer draft and
/// requesting the shell stop with a specific outcome. Keeping this an interface lets the controller be
/// unit-tested without a live Terminal.Gui application.
/// </summary>
internal interface ITuiShellHandle
{
    /// <summary>Snapshot the composer draft so it can be carried into the next shell.</summary>
    ComposerState ExportComposerState();

    /// <summary>Ask the shell's application loop to stop, recording why it is stopping.</summary>
    void RequestStop(TuiShellExit outcome);
}

/// <summary>
/// The single controller that persists across shell instances and mode switches. It owns command
/// dispatch serialization, active-turn interruption, the current semantic snapshot, composer transfer,
/// the mode-switch request, and the separate exit request. It deliberately does not own credentials,
/// the MCP manager, or the session engine — those are composed once by the interactive program and
/// shared with this controller through <see cref="AgentRunner"/>, <see cref="UiEventMailbox"/>, and
/// <see cref="ActorUiPromptService"/> so that a mode switch never rebuilds them.
/// </summary>
public sealed class TuiController
{
    /// <summary>The label shown in the status bar while the interactive startup is running.</summary>
    internal const string StartingLabel = "Starting…";

    private readonly Func<string, CancellationToken, Task<CommandResult>> dispatch;
    private readonly Func<bool> tryInterrupt;
    private readonly Func<bool> hasActiveTurn;
    private readonly IUiEventPublisher publisher;
    private readonly CancellationToken hostToken;
    private readonly object gate = new();
    private readonly AsyncLocal<bool> inDispatch = new();

    private ITuiShellHandle? shell;
    private TuiRunMode currentMode = TuiRunMode.Inline;
    private bool dispatchInFlight;
    private CancellationTokenSource? dispatchCts;
    private Task? dispatchTask;

    // The FIFO chain of out-of-band permission commands (see OnSubmitted). It only ever grows by
    // appending a continuation that awaits the previous tail, so multiple mid-turn permission commands
    // apply in submission order. It is deliberately independent of dispatchTask/dispatchInFlight so a
    // side-band command never cancels, replaces, or gates the main turn.
    private Task sidebandChain = Task.CompletedTask;

    private bool exitRequested;
    private bool startupPending;
    private TuiRunMode? pendingModeSwitch;

    /// <summary>
    /// Production constructor. The controller dispatches through <paramref name="app"/>, interrupts the
    /// active turn through <paramref name="runner"/>, publishes notices through
    /// <paramref name="mailbox"/>, and shares <paramref name="prompts"/> across shells so a pending
    /// prompt reopens after a mode switch instead of being cancelled.
    /// </summary>
    public TuiController(
        TuiApp app,
        AgentRunner runner,
        UiEventMailbox mailbox,
        ActorUiPromptService prompts,
        UiSessionSnapshot initialSnapshot,
        CancellationToken hostCancellationToken = default)
        : this(
            dispatch: (text, ct) => (app ?? throw new ArgumentNullException(nameof(app))).DispatchAsync(CommandParser.Parse(text), ct),
            tryInterrupt: (runner ?? throw new ArgumentNullException(nameof(runner))).TryInterruptActiveTurn,
            hasActiveTurn: () => runner.HasActiveTurn,
            publisher: mailbox,
            initialSnapshot: initialSnapshot,
            hostCancellationToken: hostCancellationToken)
    {
        this.App = app ?? throw new ArgumentNullException(nameof(app));
        this.Runner = runner;
        this.Mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        this.Prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
    }

    /// <summary>
    /// Test seam: inject dispatch that reports its <see cref="CommandResult"/> (so a <c>/exit</c> command
    /// stops the shell) and interrupt behavior directly, without the full engine.
    /// </summary>
    internal TuiController(
        Func<string, CancellationToken, Task<CommandResult>> dispatch,
        Func<bool> tryInterrupt,
        IUiEventPublisher publisher,
        UiSessionSnapshot initialSnapshot,
        CancellationToken hostCancellationToken = default)
        : this(dispatch, tryInterrupt, static () => false, publisher, initialSnapshot, hostCancellationToken)
    {
    }

    /// <summary>
    /// Test seam (adapter overload): a dispatch that returns a bare <see cref="Task"/> is treated as
    /// always <see cref="CommandResult.Continue"/>. Retained so existing seams stay source-compatible.
    /// </summary>
    internal TuiController(
        Func<string, CancellationToken, Task> dispatch,
        Func<bool> tryInterrupt,
        IUiEventPublisher publisher,
        UiSessionSnapshot initialSnapshot,
        CancellationToken hostCancellationToken = default)
        : this(AsContinuing(dispatch), tryInterrupt, publisher, initialSnapshot, hostCancellationToken)
    {
    }

    private TuiController(
        Func<string, CancellationToken, Task<CommandResult>> dispatch,
        Func<bool> tryInterrupt,
        Func<bool> hasActiveTurn,
        IUiEventPublisher publisher,
        UiSessionSnapshot initialSnapshot,
        CancellationToken hostCancellationToken)
    {
        this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        this.tryInterrupt = tryInterrupt ?? throw new ArgumentNullException(nameof(tryInterrupt));
        this.hasActiveTurn = hasActiveTurn ?? throw new ArgumentNullException(nameof(hasActiveTurn));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.hostToken = hostCancellationToken;
        this.CurrentSnapshot = initialSnapshot ?? throw new ArgumentNullException(nameof(initialSnapshot));
        this.CurrentComposer = ComposerState.Empty;
        this.SessionIdentity = new object();
    }

    /// <summary>The shared REPL host; null when constructed through the test seam.</summary>
    public TuiApp? App { get; }

    /// <summary>The shared agent runner; null when constructed through the test seam.</summary>
    public AgentRunner? Runner { get; }

    /// <summary>The shared UI event mailbox; null when constructed through the test seam.</summary>
    public UiEventMailbox? Mailbox { get; }

    /// <summary>The shared interactive prompt service, reused across shells; null through the test seam.</summary>
    public ActorUiPromptService? Prompts { get; }

    /// <summary>A stable object identity for the controller-owned session; unchanged across mode switches.</summary>
    public object SessionIdentity { get; }

    /// <summary>The most recent semantic snapshot, carried into the next shell after a stop.</summary>
    public UiSessionSnapshot CurrentSnapshot { get; private set; }

    /// <summary>The most recently exported composer draft, carried across mode switches.</summary>
    public ComposerState CurrentComposer { get; private set; }

    /// <summary>Whether the user requested application exit (a separate action from Ctrl-C).</summary>
    public bool ExitRequested => Volatile.Read(ref this.exitRequested);

    /// <summary>The mode requested by the last mode switch, or null when none is pending.</summary>
    public TuiRunMode? PendingModeSwitch => this.pendingModeSwitch;

    /// <summary>Whether a command/turn is currently dispatching or an agent turn is running.</summary>
    public bool HasActiveWork
    {
        get
        {
            lock (this.gate)
            {
                if (this.dispatchInFlight)
                {
                    return true;
                }
            }

            return this.hasActiveTurn();
        }
    }

    /// <summary>Test seam: the in-flight dispatch task, or null when idle.</summary>
    internal Task? CurrentDispatch
    {
        get
        {
            lock (this.gate)
            {
                return this.dispatchTask;
            }
        }
    }

    /// <summary>Test seam: the current tail of the FIFO side-band permission-command chain.</summary>
    internal Task CurrentSideband
    {
        get
        {
            lock (this.gate)
            {
                return this.sidebandChain;
            }
        }
    }

    /// <summary>Bind the controller to the shell that is now running in <paramref name="mode"/>.</summary>
    internal void AttachShell(ITuiShellHandle handle, TuiRunMode mode)
    {
        lock (this.gate)
        {
            this.shell = handle ?? throw new ArgumentNullException(nameof(handle));
            this.currentMode = mode;
        }
    }

    /// <summary>Detach the current shell (e.g. after it has stopped) so late actions are no-ops.</summary>
    internal void DetachShell()
    {
        lock (this.gate)
        {
            this.shell = null;
        }
    }

    /// <summary>
    /// Block submission until startup completes: while pending, the composer/plain loop cannot submit a
    /// turn, so a bounded mailbox cannot fill before its actor is running and no turn races MCP/setup
    /// initialization. Paired with <see cref="CompleteStartup"/>.
    /// </summary>
    internal void BeginStartup()
    {
        lock (this.gate)
        {
            this.startupPending = true;
        }

        // Surface a generic "Starting…" active-operation indicator; the status projector renders it.
        this.publisher.Publish(new ActiveOperationChangedEvent(new ActiveOperation("startup", StartingLabel, null)));
    }

    /// <summary>Re-enable submission once the interactive startup callback has finished.</summary>
    internal void CompleteStartup()
    {
        lock (this.gate)
        {
            this.startupPending = false;
        }

        this.publisher.Publish(new ActiveOperationChangedEvent(null));
    }

    /// <summary>Whether startup is still running and submission is therefore blocked.</summary>
    internal bool StartupPending
    {
        get
        {
            lock (this.gate)
            {
                return this.startupPending;
            }
        }
    }

    /// <summary>Persist the actor's latest snapshot so the next shell is seeded from it.</summary>
    public void CaptureSnapshot(UiSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        this.CurrentSnapshot = snapshot;
    }

    /// <summary>Interrupt the active turn, if any. Returns false when nothing was running.</summary>
    public bool TryInterruptActiveTurn() => this.tryInterrupt();

    /// <summary>Request application exit and, if a shell is attached, stop it with an exit outcome.</summary>
    public void RequestExit()
    {
        Volatile.Write(ref this.exitRequested, true);
        this.CurrentShell()?.RequestStop(TuiShellExit.Exited);
    }

    /// <summary>
    /// Export the composer from the current shell and request a switch to <paramref name="target"/>,
    /// carrying the draft. Returns the switch outcome the shell run should surface to the host.
    /// </summary>
    public TuiShellExit RequestModeSwitch(TuiRunMode target)
    {
        var handle = this.CurrentShell();
        var composer = handle?.ExportComposerState() ?? this.CurrentComposer;
        this.CurrentComposer = composer;
        this.pendingModeSwitch = target;

        var outcome = TuiShellExit.SwitchTo(target, composer);
        handle?.RequestStop(outcome);
        return outcome;
    }

    /// <summary>
    /// Schedule a submitted prompt/command for dispatch on a controller-owned task and return
    /// immediately so the Terminal.Gui UI loop keeps rendering. While a dispatch is already in flight,
    /// ordinary submissions are rejected — except the safe live permission commands (<c>/yolo</c>,
    /// <c>/permissions [mode]</c>, <c>/mode [mode]</c>), which run out-of-band on a serialized side-band
    /// chain so the user can change permission mode mid-turn. Completion (or failure) re-enables submission.
    /// </summary>
    public void OnSubmitted(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        CancellationToken token;
        lock (this.gate)
        {
            // Exit/startup always reject every submission, including permission commands.
            if (Volatile.Read(ref this.exitRequested) || this.startupPending)
            {
                return;
            }

            if (this.dispatchInFlight)
            {
                // Busy: only a safe live permission command may run out-of-band; everything else is
                // rejected exactly as before. The main turn is left completely untouched.
                if (LivePermissionCommands.IsLivePermissionCommand(CommandParser.Parse(text)))
                {
                    this.QueueSidebandCommand(text);
                }

                return;
            }

            this.dispatchInFlight = true;
            this.dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(this.hostToken);
            token = this.dispatchCts.Token;
            this.dispatchTask = Task.Run(() => this.RunDispatchAsync(text, token));
        }
    }

    /// <summary>
    /// Append <paramref name="text"/> to the side-band permission chain. Called under <see cref="gate"/>;
    /// it only schedules a continuation (never awaits), so no lock is held across an await. Each command
    /// awaits the previous tail before running, giving deterministic FIFO application.
    /// </summary>
    private void QueueSidebandCommand(string text)
    {
        var previous = this.sidebandChain;
        this.sidebandChain = Task.Run(() => this.RunSidebandAsync(previous, text));
    }

    /// <summary>
    /// Run one out-of-band permission command after its predecessor completes. It reuses the injected
    /// dispatch delegate so output, session-metadata events, live permission state, and validation wording
    /// stay a single source of truth. It never touches the main dispatch's state and observes all of its
    /// own faults so nothing surfaces as an unobserved task exception during shutdown.
    /// </summary>
    private async Task RunSidebandAsync(Task previous, string text)
    {
        try
        {
            // FIFO: wait for the previous side-band command; its own faults are already observed there.
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // A failed predecessor must not prevent this queued command from running.
        }

        // Host shutting down: do not start executing queued commands.
        if (this.hostToken.IsCancellationRequested)
        {
            return;
        }

        // Mark the flow (same AsyncLocal the main dispatch uses) so a re-entrant WaitForDispatchAsync
        // called from inside the command never self-joins and deadlocks.
        this.inDispatch.Value = true;
        try
        {
            await this.dispatch(text, this.hostToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host token was cancelled mid-command during shutdown; nothing to report.
        }
        catch (Exception ex)
        {
            this.PublishDispatchError(ex);
        }
    }

    /// <summary>Route a named shell action to its controller behavior.</summary>
    public Task HandleActionAsync(UiAction action)
    {
        switch (action)
        {
            case UiAction.Interrupt:
                this.TryInterruptActiveTurn();
                break;
            case UiAction.Exit:
                this.RequestExit();
                break;
            case UiAction.ToggleMode:
                this.RequestModeSwitch(this.ToggleTarget());
                break;
            default:
                break;
        }

        return Task.CompletedTask;
    }

    private async Task RunDispatchAsync(string text, CancellationToken token)
    {
        // Mark the async flow so a re-entrant WaitForDispatchAsync (called from within the dispatch)
        // never self-joins and deadlocks.
        this.inDispatch.Value = true;
        try
        {
            var result = await this.dispatch(text, token).ConfigureAwait(false);

            // A `/exit` command reports ShouldExit; honor it exactly as the plain/Spectre loops break on
            // the same result, stopping the attached shell so Terminal.Gui leaves cleanly.
            if (result.ShouldExit)
            {
                this.RequestExit();
            }
        }
        catch (OperationCanceledException)
        {
            // Interrupted or shut down; the turn already published its own interruption.
        }
        catch (Exception ex)
        {
            this.PublishDispatchError(ex);
        }
        finally
        {
            lock (this.gate)
            {
                this.dispatchInFlight = false;
                this.dispatchCts?.Dispose();
                this.dispatchCts = null;
                this.dispatchTask = null;
            }
        }
    }

    /// <summary>
    /// Publish a dispatch fault as an error notice, but never let a late publish surface as an
    /// unobserved fault: during shutdown the mailbox may already be (or is about to be) disposed, and
    /// dropping a diagnostic then is safe. The shutdown-specific <see cref="ObjectDisposedException"/> is
    /// handled narrowly so a genuine bug in the publisher is still not swallowed silently otherwise.
    /// </summary>
    private void PublishDispatchError(Exception ex)
    {
        if (this.hostToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            this.publisher.Publish(new AgentErrorEvent(ex.Message));
        }
        catch (ObjectDisposedException)
        {
            // The mailbox was disposed concurrently with shutdown; the diagnostic is safely dropped.
        }
    }

    /// <summary>
    /// Observe the in-flight dispatch and the side-band permission chain (if any) so a host shutdown can
    /// await them before the actor is flushed and the mailbox disposed — otherwise a late producer could
    /// publish into a disposed mailbox. Bounded by <paramref name="cancellationToken"/> and safe against
    /// races: it returns at once when idle, when work is already completing, or when invoked from within a
    /// dispatch/side-band task itself (a self-join would otherwise deadlock). It never rethrows those
    /// tasks' own faults, which are handled inside <see cref="RunDispatchAsync"/> and
    /// <see cref="RunSidebandAsync"/>.
    /// </summary>
    public async Task WaitForDispatchAsync(CancellationToken cancellationToken = default)
    {
        if (this.inDispatch.Value)
        {
            return;
        }

        Task? current;
        Task sideband;
        lock (this.gate)
        {
            current = this.dispatchTask;
            sideband = this.sidebandChain;
        }

        await ObserveQuietlyAsync(current, cancellationToken).ConfigureAwait(false);
        await ObserveQuietlyAsync(sideband, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Await <paramref name="task"/> for teardown, absorbing cancellation and its own faults.</summary>
    private static async Task ObserveQuietlyAsync(Task? task, CancellationToken cancellationToken)
    {
        if (task is null || task.IsCompleted)
        {
            return;
        }

        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Bounded: the shutdown budget elapsed or was cancelled; proceed with teardown regardless.
        }
        catch (Exception)
        {
            // The awaited task owns its faults; observing it here must never rethrow into shutdown.
        }
    }

    /// <summary>Adapt a result-less dispatch into one that always reports <see cref="CommandResult.Continue"/>.</summary>
    private static Func<string, CancellationToken, Task<CommandResult>> AsContinuing(
        Func<string, CancellationToken, Task> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return async (text, token) =>
        {
            await dispatch(text, token).ConfigureAwait(false);
            return CommandResult.Continue;
        };
    }

    private TuiRunMode ToggleTarget()
    {
        lock (this.gate)
        {
            return this.currentMode == TuiRunMode.Fullscreen ? TuiRunMode.Inline : TuiRunMode.Fullscreen;
        }
    }

    private ITuiShellHandle? CurrentShell()
    {
        lock (this.gate)
        {
            return this.shell;
        }
    }
}
