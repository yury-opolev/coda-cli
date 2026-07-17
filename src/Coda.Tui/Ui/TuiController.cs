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
    /// <summary>The idle Ctrl-C notice; Ctrl-C never exits, so this points users at an explicit exit.</summary>
    internal const string IdleNotification = "Nothing is running; use /exit or Ctrl+D to exit.";

    /// <summary>The label shown in the status bar while the interactive startup is running.</summary>
    internal const string StartingLabel = "Starting…";

    private readonly Func<string, CancellationToken, Task> dispatch;
    private readonly Func<bool> tryInterrupt;
    private readonly Func<bool> hasActiveTurn;
    private readonly IUiEventPublisher publisher;
    private readonly CancellationToken hostToken;
    private readonly object gate = new();

    private ITuiShellHandle? shell;
    private TuiRunMode currentMode = TuiRunMode.Inline;
    private bool dispatchInFlight;
    private CancellationTokenSource? dispatchCts;
    private Task? dispatchTask;
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

    /// <summary>Test seam: inject dispatch and interrupt behavior directly, without the full engine.</summary>
    internal TuiController(
        Func<string, CancellationToken, Task> dispatch,
        Func<bool> tryInterrupt,
        IUiEventPublisher publisher,
        UiSessionSnapshot initialSnapshot,
        CancellationToken hostCancellationToken = default)
        : this(dispatch, tryInterrupt, static () => false, publisher, initialSnapshot, hostCancellationToken)
    {
    }

    private TuiController(
        Func<string, CancellationToken, Task> dispatch,
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

    /// <summary>
    /// Handle Ctrl-C: interrupt the active turn first; when idle, publish a short notice and never
    /// exit. Returns whether an active turn was interrupted.
    /// </summary>
    public bool HandleCtrlC()
    {
        if (this.tryInterrupt())
        {
            return true;
        }

        this.publisher.Publish(new NotificationEvent(IdleNotification, UiNotificationLevel.Information));
        return false;
    }

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
    /// immediately so the Terminal.Gui UI loop keeps rendering. Additional submits are rejected while a
    /// dispatch is in flight; completion (or failure) re-enables submission.
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
            if (Volatile.Read(ref this.exitRequested) || this.dispatchInFlight || this.startupPending)
            {
                return;
            }

            this.dispatchInFlight = true;
            this.dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(this.hostToken);
            token = this.dispatchCts.Token;
            this.dispatchTask = Task.Run(() => this.RunDispatchAsync(text, token));
        }
    }

    /// <summary>Route a named shell action to its controller behavior.</summary>
    public Task HandleActionAsync(UiAction action)
    {
        switch (action)
        {
            case UiAction.Interrupt:
                this.HandleCtrlC();
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
        try
        {
            await this.dispatch(text, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Interrupted or shut down; the turn already published its own interruption.
        }
        catch (Exception ex)
        {
            this.publisher.Publish(new AgentErrorEvent(ex.Message));
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
