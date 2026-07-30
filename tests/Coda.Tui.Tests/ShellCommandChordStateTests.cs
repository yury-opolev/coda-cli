using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class ShellCommandChordStateTests
{
    // -----------------------------------------------------------------------
    // Three-press Esc interrupt chord
    // -----------------------------------------------------------------------

    [Fact]
    public void Escape_press_one_shows_twice_more_hint()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);

        var first = state.HandleEscape(hasActiveWork: true);

        Assert.Equal(
            new OperationalStatus(
                "Press Esc twice more to stop",
                OperationalTone.Warning,
                Animated: false),
            first.Hint);
        Assert.Equal(ShellChordAction.None, first.Action);
        Assert.True(first.Consumed);
        Assert.Equal(ShellChordAction.Interrupt, state.ArmedAction);
    }

    [Fact]
    public void Escape_press_two_shows_again_hint()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);
        state.HandleEscape(hasActiveWork: true);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        var second = state.HandleEscape(hasActiveWork: true);

        Assert.Equal(
            new OperationalStatus(
                "Press Esc again to stop",
                OperationalTone.Warning,
                Animated: false),
            second.Hint);
        Assert.Equal(ShellChordAction.None, second.Action);
        Assert.True(second.Consumed);
        Assert.Equal(ShellChordAction.Interrupt, state.ArmedAction);
    }

    [Fact]
    public void Escape_three_presses_within_window_arms_the_stop_confirmation()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);
        state.HandleEscape(hasActiveWork: true);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        state.HandleEscape(hasActiveWork: true);
        clock.Advance(TimeSpan.FromMilliseconds(100));

        var third = state.HandleEscape(hasActiveWork: true);

        // The gesture asks rather than stops: nothing fires until the confirmation is answered.
        Assert.Equal(ShellChordAction.None, third.Action);
        Assert.Equal(ShellChordAction.ConfirmStop, state.ArmedAction);
        Assert.Contains("Stop the current turn?", third.Hint!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirming_an_armed_stop_fires_interrupt()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);
        PressEscape(state, clock, 3);

        var confirmed = state.HandleConfirmStop();

        Assert.True(confirmed.Consumed);
        Assert.Equal(ShellChordAction.Interrupt, confirmed.Action);
        Assert.Equal(ShellChordAction.None, state.ArmedAction);
    }

    [Fact]
    public void Confirming_without_an_armed_stop_consumes_nothing()
    {
        var state = new ShellCommandChordState(new ManualTimeProvider());

        var result = state.HandleConfirmStop();

        // Enter must keep its ordinary meaning when no confirmation is pending.
        Assert.False(result.Consumed);
        Assert.Equal(ShellChordAction.None, result.Action);
    }

    [Fact]
    public void Escape_declines_an_armed_stop_confirmation()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);
        PressEscape(state, clock, 3);

        var declined = state.HandleEscape(hasActiveWork: true);

        // A fourth press is what a user hammering Esc actually does; it must decline, not re-arm.
        Assert.True(declined.Consumed);
        Assert.Equal(ShellChordAction.None, declined.Action);
        Assert.Equal(ShellChordAction.None, state.ArmedAction);
        Assert.Null(state.CurrentHint);
    }

    [Fact]
    public void An_armed_confirmation_can_be_declined_after_the_work_finished()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);
        PressEscape(state, clock, 3);

        // The turn completed while the confirmation was on screen: Esc must still clear it, so the
        // hint can never outlive the work it belongs to.
        var declined = state.HandleEscape(hasActiveWork: false);

        Assert.True(declined.Consumed);
        Assert.Equal(ShellChordAction.None, state.ArmedAction);
    }

    [Fact]
    public void An_armed_confirmation_expires_on_its_own_longer_window()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);
        PressEscape(state, clock, 3);

        // Still armed well past the three-press chord window...
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(state.Expire());
        Assert.Equal(ShellChordAction.ConfirmStop, state.ArmedAction);

        // ...but not past its own.
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(state.Expire());
        Assert.Equal(ShellChordAction.None, state.ArmedAction);
    }

    [Fact]
    public void Remaining_window_counts_down_from_the_first_press()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);
        state.HandleEscape(hasActiveWork: true);

        clock.Advance(TimeSpan.FromMilliseconds(600));
        state.HandleEscape(hasActiveWork: true);

        // A mid-sequence press must not restart the clock, or the hint would outlive the chord.
        Assert.Equal(TimeSpan.FromMilliseconds(900), state.RemainingWindow);
    }

    [Fact]
    public void Two_escape_presses_do_not_arm_the_confirmation()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);
        state.HandleEscape(hasActiveWork: true);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        var second = state.HandleEscape(hasActiveWork: true);

        Assert.Equal(ShellChordAction.None, second.Action);
        Assert.Equal(ShellChordAction.Interrupt, state.ArmedAction);
        Assert.Equal("Press Esc again to stop", second.Hint!.Text);
    }

    private static void PressEscape(ShellCommandChordState state, ManualTimeProvider clock, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            state.HandleEscape(hasActiveWork: true);
        }
    }

    [Fact]
    public void Expired_window_rearms_from_press_one_instead_of_firing()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);
        state.HandleEscape(hasActiveWork: true);
        state.HandleEscape(hasActiveWork: true); // two presses in

        clock.Advance(TimeSpan.FromMilliseconds(1501)); // window lapses

        var result = state.HandleEscape(hasActiveWork: true);

        // Re-arms from press 1 — stale sequence never fires.
        Assert.Equal(ShellChordAction.None, result.Action);
        Assert.Equal("Press Esc twice more to stop", result.Hint!.Text);
    }

    [Fact]
    public void Escape_with_no_active_work_consumes_nothing_and_resets()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);
        state.HandleEscape(hasActiveWork: true); // arm first

        var result = state.HandleEscape(hasActiveWork: false);

        Assert.False(result.Consumed);
        Assert.Equal(ShellChordAction.None, result.Action);
        Assert.Null(result.Hint);
        Assert.Equal(ShellChordAction.None, state.ArmedAction);
        Assert.Null(state.CurrentHint);
    }

    // -----------------------------------------------------------------------
    // Ctrl+C exit chord — unchanged two-press behaviour
    // -----------------------------------------------------------------------

    [Fact]
    public void Ctrl_c_arms_then_exits_within_1500ms()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);

        var first = state.HandleCtrlC();
        clock.Advance(TimeSpan.FromMilliseconds(1499));
        var second = state.HandleCtrlC();

        Assert.Equal("Press Ctrl+C again to exit", first.Hint!.Text);
        Assert.Equal(ShellChordAction.Exit, second.Action);
    }

    // -----------------------------------------------------------------------
    // Reset
    // -----------------------------------------------------------------------

    [Fact]
    public void Reset_clears_hint_and_armed_action()
    {
        var state = new ShellCommandChordState(new ManualTimeProvider());
        state.HandleCtrlC();

        state.Reset();

        Assert.Null(state.CurrentHint);
        Assert.Equal(ShellChordAction.None, state.ArmedAction);
    }
}
