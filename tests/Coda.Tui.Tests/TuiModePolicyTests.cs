using Coda.Tui.Ui.Mode;

namespace Coda.Tui.Tests;

public sealed class TuiModePolicyTests
{
    [Fact]
    public void Auto_uses_plain_for_redirected_output()
    {
        var caps = new TerminalCapabilities(false, true, 120, 40, true);
        var decision = TuiModePolicy.SelectInitial(new TuiLaunchOptions(TuiPreference.Auto, false, [], null), caps);

        Assert.Equal(TuiRunMode.Plain, decision.Mode);
        Assert.Null(decision.Error);
    }

    [Theory]
    [InlineData(59, 12)]
    [InlineData(60, 11)]
    public void Auto_uses_plain_below_minimum_size(int width, int height)
    {
        var caps = new TerminalCapabilities(false, false, width, height, true);
        var decision = TuiModePolicy.SelectInitial(new TuiLaunchOptions(TuiPreference.Auto, false, [], null), caps);

        Assert.Equal(TuiRunMode.Plain, decision.Mode);
    }

    [Fact]
    public void Explicit_interactive_mode_reports_too_small()
    {
        var caps = new TerminalCapabilities(false, false, 59, 12, true);
        var decision = TuiModePolicy.SelectInitial(new TuiLaunchOptions(TuiPreference.Inline, false, [], null), caps);

        Assert.Null(decision.Mode);
        Assert.Equal("Terminal.Gui requires at least 60 columns by 12 rows; current size is 59x12.", decision.Error);
    }

    [Fact]
    public void Fullscreen_fallback_order_is_complete()
    {
        Assert.Equal(
            [TuiRunMode.Fullscreen, TuiRunMode.Inline, TuiRunMode.Spectre, TuiRunMode.Plain],
            TuiModePolicy.FallbacksFrom(TuiRunMode.Fullscreen));
    }
}
