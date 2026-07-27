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
    public void CODA_TUI_DRIVER_ansi_pins_ansi_driver_explicitly()
    {
        // CODA_TUI_DRIVER=ansi selects the diffing output (ansi resolves to the diffing layer).
        var env = new Dictionary<string, string?>
        {
            ["WT_SESSION"] = "abc-123",
            ["CODA_TUI_DRIVER"] = "ansi",
        };

        var driver = TerminalInputCompatibility.SelectDriverName(env.GetValueOrDefault, isWindows: true);

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
    public void Driver_override_forces_named_driver_over_windows_terminal_default()
    {
        var env = new Dictionary<string, string?>
        {
            ["WT_SESSION"] = "abc-123",
            ["CODA_TUI_DRIVER"] = "windows",
        };

        var driver = TerminalInputCompatibility.SelectDriverName(env.GetValueOrDefault, isWindows: true);

        Assert.Equal(DriverRegistry.Names.WINDOWS, driver);
    }

    [Fact]
    public void Driver_override_default_forces_platform_default()
    {
        var env = new Dictionary<string, string?>
        {
            ["WT_SESSION"] = "abc-123",
            ["CODA_TUI_DRIVER"] = "  default  ",
        };

        var driver = TerminalInputCompatibility.SelectDriverName(env.GetValueOrDefault, isWindows: true);

        Assert.Null(driver);
    }

    [Theory]
    [InlineData("ansi")]
    [InlineData("ANSI")]
    [InlineData("Ansi")]
    public void ShouldUseDiffingOutput_is_true_for_ansi_names(string name)
    {
        Assert.True(TerminalInputCompatibility.ShouldUseDiffingOutput(name, getEnv: _ => null));
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("dotnet")]
    public void ShouldUseDiffingOutput_is_false_for_non_ansi_names(string? name)
    {
        Assert.False(TerminalInputCompatibility.ShouldUseDiffingOutput(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ShouldUseDiffingOutput_is_true_when_driver_is_null_or_blank_and_default_is_ansi(string? name)
    {
        // A null/blank name means "let Terminal.Gui pick the platform default". When the
        // platform default resolves to the ANSI driver, the diffing output should activate.
        Assert.True(TerminalInputCompatibility.ShouldUseDiffingOutput(
            name,
            getEnv: _ => null,
            getDefaultDriverName: () => DriverRegistry.Names.ANSI));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ShouldUseDiffingOutput_is_false_when_driver_is_null_or_blank_and_default_is_dotnet(string? name)
    {
        Assert.False(TerminalInputCompatibility.ShouldUseDiffingOutput(
            name,
            getDefaultDriverName: () => DriverRegistry.Names.DOTNET));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    [InlineData("FALSE")]
    [InlineData("  off  ")]
    public void CODA_TUI_DIFF_opt_out_values_disable_diffing_output(string optOutValue)
    {
        Assert.False(TerminalInputCompatibility.ShouldUseDiffingOutput(
            DriverRegistry.Names.ANSI,
            getEnv: key => key == "CODA_TUI_DIFF" ? optOutValue : null));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData(null)]
    [InlineData("")]
    public void CODA_TUI_DIFF_non_opt_out_values_keep_diffing_enabled(string? optOutValue)
    {
        Assert.True(TerminalInputCompatibility.ShouldUseDiffingOutput(
            DriverRegistry.Names.ANSI,
            getEnv: key => key == "CODA_TUI_DIFF" ? optOutValue : null));
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
