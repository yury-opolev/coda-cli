using Coda.Tui.Ui.State;

namespace Coda.Tui.Ui.Shells;

/// <summary>The monotonic chord a key press is arming or firing.</summary>
internal enum ShellChordAction
{
    None,
    Interrupt,
    Exit,
}

/// <summary>
/// The outcome of feeding a key into <see cref="ShellCommandChordState"/>: whether the state consumed
/// the key, which action fired (if any), and the operational hint to display while the second press is
/// still awaited.
/// </summary>
internal readonly record struct ShellChordResult(
    bool Consumed,
    ShellChordAction Action,
    OperationalStatus? Hint);

/// <summary>
/// A deterministic, clock-driven state machine for the safe two-press interrupt/exit chords. The first
/// Esc (while work is interruptible) arms an interrupt and shows a warning hint; a second Esc within
/// <see cref="InterruptWindow"/> fires <see cref="ShellChordAction.Interrupt"/>. The first Ctrl+C arms an
/// exit and shows a warning hint; a second Ctrl+C within <see cref="ExitWindow"/> fires
/// <see cref="ShellChordAction.Exit"/>. A press after the window lapses simply re-arms, so a chord can
/// never fire from a stale first press. The injected <see cref="TimeProvider"/> makes the windows testable
/// without real time.
/// </summary>
internal sealed class ShellCommandChordState
{
    internal static readonly TimeSpan InterruptWindow = TimeSpan.FromMilliseconds(800);
    internal static readonly TimeSpan ExitWindow = TimeSpan.FromMilliseconds(1500);

    private readonly TimeProvider clock;
    private long armedAt;

    public ShellCommandChordState(TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The action currently armed by a first press, or <see cref="ShellChordAction.None"/>.</summary>
    internal ShellChordAction ArmedAction { get; private set; }

    /// <summary>The hint to display while an armed chord awaits its confirming second press, else null.</summary>
    internal OperationalStatus? CurrentHint { get; private set; }

    internal ShellChordResult HandleEscape(bool hasActiveWork)
    {
        if (!hasActiveWork)
        {
            this.Reset();
            return new(false, ShellChordAction.None, null);
        }

        return this.Handle(
            ShellChordAction.Interrupt,
            InterruptWindow,
            new OperationalStatus(
                "Press Esc again to interrupt",
                OperationalTone.Warning,
                false));
    }

    internal ShellChordResult HandleCtrlC() =>
        this.Handle(
            ShellChordAction.Exit,
            ExitWindow,
            new OperationalStatus(
                "Press Ctrl+C again to exit",
                OperationalTone.Warning,
                false));

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

        var window = this.ArmedAction == ShellChordAction.Interrupt
            ? InterruptWindow
            : ExitWindow;
        if (this.clock.GetElapsedTime(this.armedAt, this.clock.GetTimestamp()) <= window)
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
        this.CurrentHint = hint;
        return new(true, ShellChordAction.None, hint);
    }
}
