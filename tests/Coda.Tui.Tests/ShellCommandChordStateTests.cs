using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class ShellCommandChordStateTests
{
    [Fact]
    public void Escape_arms_then_interrupts_within_800ms()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);

        var first = state.HandleEscape(hasActiveWork: true);
        Assert.Equal(
            new OperationalStatus(
                "Press Esc again to interrupt",
                OperationalTone.Warning,
                Animated: false),
            first.Hint);
        Assert.Equal(ShellChordAction.None, first.Action);

        clock.Advance(TimeSpan.FromMilliseconds(799));
        var second = state.HandleEscape(hasActiveWork: true);
        Assert.Equal(ShellChordAction.Interrupt, second.Action);
        Assert.Null(second.Hint);
    }

    [Fact]
    public void Expired_escape_window_rearms_instead_of_interrupting()
    {
        var clock = new ManualTimeProvider();
        var state = new ShellCommandChordState(clock);
        state.HandleEscape(hasActiveWork: true);

        clock.Advance(TimeSpan.FromMilliseconds(801));
        var result = state.HandleEscape(hasActiveWork: true);

        Assert.Equal(ShellChordAction.None, result.Action);
        Assert.Equal("Press Esc again to interrupt", result.Hint!.Text);
    }

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
