using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.Input;
using Terminal.Gui;
using Terminal.Gui.Drivers;

namespace Coda.Tui.Tests;

public sealed class TerminalInputCompatibilityTests
{
    [Fact]
    public void Windows_terminal_prefers_ansi_driver()
    {
        var env = new Dictionary<string, string?> { ["WT_SESSION"] = "abc-123" };

        var driver = TerminalInputCompatibility.SelectDriverName(
            env.GetValueOrDefault,
            isWindows: true);

        Assert.Equal(DriverRegistry.Names.ANSI, driver);
    }

    [Fact]
    public void Windows_terminal_without_session_keeps_default_driver()
    {
        var env = new Dictionary<string, string?>();

        var driver = TerminalInputCompatibility.SelectDriverName(
            env.GetValueOrDefault,
            isWindows: true);

        Assert.Null(driver);
    }

    [Fact]
    public void Non_windows_terminal_keeps_default_driver()
    {
        var env = new Dictionary<string, string?> { ["WT_SESSION"] = "abc-123" };

        var driver = TerminalInputCompatibility.SelectDriverName(
            env.GetValueOrDefault,
            isWindows: false);

        Assert.Null(driver);
    }

    [Fact]
    public void Csi_13_2u_is_decoded_as_shift_enter_by_the_ansi_parser()
    {
        const string sequence = "\u001b[13;2u";

        var pattern = new AnsiKeyboardParser().IsKeyboard(sequence, isLastMinute: false);

        Assert.NotNull(pattern);
        var key = pattern!.GetKey(sequence) ?? throw new InvalidOperationException("ANSI parser returned no key.");
        Assert.Equal(Key.Enter.WithShift, key);
        Assert.Equal(
            UiAction.InsertNewline,
            UiActionMap.Map(
                TerminalInputCompatibility.NormalizeModifiedEnter(key),
                new UiInputContext(false, false, true, true)));
    }

    [Fact]
    public void Native_modified_enter_passes_through_unchanged()
    {
        Assert.Equal(
            Key.Enter.WithShift,
            TerminalInputCompatibility.NormalizeModifiedEnter(Key.Enter.WithShift));
    }

    [Fact]
    public void Plain_enter_is_not_altered()
    {
        Assert.Equal(Key.Enter, TerminalInputCompatibility.NormalizeModifiedEnter(Key.Enter));
    }

    [Fact]
    public void Enter_fallbacks_and_plain_enter_remain_unchanged()
    {
        var context = new UiInputContext(false, false, true, true);

        Assert.Equal(UiAction.InsertNewline, UiActionMap.Map(Key.Enter.WithCtrl, context));
        Assert.Equal(UiAction.InsertNewline, UiActionMap.Map(Key.J.WithCtrl, context));
        Assert.Equal(UiAction.Submit, UiActionMap.Map(Key.Enter, context));
    }
}
