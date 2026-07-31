using Coda.Tui.Ui.State;

namespace Coda.Tui.Ui.Shells;

/// <summary>The monotonic chord a key press is arming or firing.</summary>
internal enum ShellChordAction
{
    None,
    Interrupt,
    Exit,

    /// <summary>
    /// The stop chord has completed its presses and is awaiting an explicit yes/no. Confirming fires
    /// <see cref="Interrupt"/>; declining (or letting the window lapse) leaves the turn untouched.
    /// </summary>
    ConfirmStop,
}

/// <summary>
/// The outcome of feeding a key into <see cref="ShellCommandChordState"/>: whether the state consumed
/// the key, which action fired (if any), and the operational hint to display while the chord is
/// still awaiting further presses.
/// </summary>
internal readonly record struct ShellChordResult(
    bool Consumed,
    ShellChordAction Action,
    OperationalStatus? Hint);

/// <summary>
/// A deterministic, clock-driven state machine for the shell's safe key chords.
/// <list type="bullet">
///   <item><b>Stop.</b> Three Esc presses, all within <see cref="InterruptWindow"/> of the first, arm a
///     confirmation; <see cref="HandleConfirmStop"/> then fires <see cref="ShellChordAction.Interrupt"/>.
///     The confirmation is deliberately keyboard-local rather than a modal prompt: a stop is requested
///     <em>while a turn is running</em>, which is exactly when the turn may raise prompts of its own.</item>
///   <item><b>Exit.</b> Two Ctrl+C presses within <see cref="ExitWindow"/> fire
///     <see cref="ShellChordAction.Exit"/>.</item>
/// </list>
/// A press after a window lapses re-arms from the first press, so a chord can never fire from a stale
/// sequence. The injected <see cref="TimeProvider"/> makes every window testable without real time.
/// </summary>
internal sealed class ShellCommandChordState
{
    /// <summary>How long the three Esc presses have to complete, measured from the first.</summary>
    internal static readonly TimeSpan InterruptWindow = TimeSpan.FromMilliseconds(1500);

    internal static readonly TimeSpan ExitWindow = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// How long the stop confirmation stays on screen. Generous compared with the chord windows because
    /// this one asks the user to read and decide, not to complete a gesture.
    /// </summary>
    internal static readonly TimeSpan ConfirmStopWindow = TimeSpan.FromSeconds(10);

    /// <summary>Esc presses required to reach the stop confirmation.</summary>
    private const int EscapePressesToStop = 2;

    private readonly TimeProvider clock;
    private long armedAt;
    private int escapePresses;

    public ShellCommandChordState(TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The action currently armed by a first press, or <see cref="ShellChordAction.None"/>.</summary>
    internal ShellChordAction ArmedAction { get; private set; }

    /// <summary>The hint to display while an armed chord awaits its confirming press, else null.</summary>
    internal OperationalStatus? CurrentHint { get; private set; }

    /// <summary>The window the currently armed action is measured against.</summary>
    internal TimeSpan ArmedWindow => WindowFor(this.ArmedAction);

    /// <summary>
    /// Time left before the armed chord lapses, or <see cref="TimeSpan.Zero"/> when nothing is armed.
    /// The caller schedules its expiry timeout against this rather than the full window, because a
    /// mid-sequence press does not restart the clock.
    /// </summary>
    internal TimeSpan RemainingWindow
    {
        get
        {
            if (this.ArmedAction == ShellChordAction.None)
            {
                return TimeSpan.Zero;
            }

            var elapsed = this.clock.GetElapsedTime(this.armedAt, this.clock.GetTimestamp());
            var remaining = this.ArmedWindow - elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Handles an Esc press. Two presses within <see cref="InterruptWindow"/> of the first arm the stop
    /// confirmation; a press while that confirmation is armed declines it. A press after the window lapses
    /// re-arms from the first. Returns <c>Consumed: false</c> when <paramref name="hasActiveWork"/> is
    /// false, resetting any in-progress sequence so Esc keeps its ordinary dismiss meaning when idle.
    /// </summary>
    internal ShellChordResult HandleEscape(bool hasActiveWork)
    {
        // Declining an armed confirmation must work even once the work has finished, so this is checked
        // before the idle guard — otherwise the hint could outlive the turn it belongs to.
        if (this.ArmedAction == ShellChordAction.ConfirmStop)
        {
            this.Reset();
            return new(true, ShellChordAction.None, null);
        }

        if (!hasActiveWork)
        {
            this.Reset();
            return new(false, ShellChordAction.None, null);
        }

        var now = this.clock.GetTimestamp();
        var continuing = this.ArmedAction == ShellChordAction.Interrupt &&
            this.clock.GetElapsedTime(this.armedAt, now) <= InterruptWindow;

        if (!continuing)
        {
            // First press, or a re-arm after the window lapsed. Only here does the clock start.
            this.ArmedAction = ShellChordAction.Interrupt;
            this.armedAt = now;
            this.escapePresses = 1;
            return this.Arm(Hint(RemainingPressesHint(1)));
        }

        this.escapePresses++;
        if (this.escapePresses < EscapePressesToStop)
        {
            return this.Arm(Hint(RemainingPressesHint(this.escapePresses)));
        }

        // The gesture is complete: hand over to an explicit confirmation rather than stopping outright.
        this.ArmedAction = ShellChordAction.ConfirmStop;
        this.armedAt = now;
        this.escapePresses = 0;
        return this.Arm(Hint("Stop the current turn? Enter to stop · Esc to keep going"));
    }

    /// <summary>
    /// The prompt shown after <paramref name="pressesSoFar"/> presses, phrased for however many remain.
    /// Derived from <see cref="EscapePressesToStop"/> so the wording can never drift from the chord.
    /// </summary>
    private static string RemainingPressesHint(int pressesSoFar) =>
        EscapePressesToStop - pressesSoFar == 1
            ? "Press Esc again to stop"
            : $"Press Esc {EscapePressesToStop - pressesSoFar} more times to stop";

    /// <summary>
    /// Confirms an armed stop, firing <see cref="ShellChordAction.Interrupt"/>. Returns
    /// <c>Consumed: false</c> when no confirmation is armed, so the key keeps its ordinary meaning.
    /// </summary>
    internal ShellChordResult HandleConfirmStop()
    {
        if (this.ArmedAction != ShellChordAction.ConfirmStop)
        {
            return new(false, ShellChordAction.None, null);
        }

        this.Reset();
        return new(true, ShellChordAction.Interrupt, null);
    }

    internal ShellChordResult HandleCtrlC() =>
        this.Handle(
            ShellChordAction.Exit,
            ExitWindow,
            Hint("Press Ctrl+C again to exit"));

    /// <summary>
    /// Disarm a chord whose window has lapsed. Returns true when an expired arm was cleared, letting the
    /// caller restore the projected status. A still-live or already-disarmed chord is left untouched.
    /// </summary>
    internal bool Expire()
    {
        if (this.ArmedAction == ShellChordAction.None)
        {
            return false;
        }

        if (this.clock.GetElapsedTime(this.armedAt, this.clock.GetTimestamp()) <= this.ArmedWindow)
        {
            return false;
        }

        this.Reset();
        return true;
    }

    internal void Reset()
    {
        this.ArmedAction = ShellChordAction.None;
        this.CurrentHint = null;
        this.armedAt = 0;
        this.escapePresses = 0;
    }

    private static TimeSpan WindowFor(ShellChordAction action) => action switch
    {
        ShellChordAction.Interrupt => InterruptWindow,
        ShellChordAction.ConfirmStop => ConfirmStopWindow,
        ShellChordAction.Exit => ExitWindow,
        _ => TimeSpan.Zero,
    };

    private static OperationalStatus Hint(string text) =>
        new(text, OperationalTone.Warning, false);

    /// <summary>Pins <paramref name="hint"/> as the current hint and returns an arming result.</summary>
    private ShellChordResult Arm(OperationalStatus hint)
    {
        this.CurrentHint = hint;
        return new(true, ShellChordAction.None, hint);
    }

    private ShellChordResult Handle(
        ShellChordAction action,
        TimeSpan window,
        OperationalStatus hint)
    {
        var now = this.clock.GetTimestamp();
        if (this.ArmedAction == action &&
            this.clock.GetElapsedTime(this.armedAt, now) <= window)
        {
            this.Reset();
            return new(true, action, null);
        }

        this.ArmedAction = action;
        this.armedAt = now;
        this.escapePresses = 0;
        return this.Arm(hint);
    }
}
