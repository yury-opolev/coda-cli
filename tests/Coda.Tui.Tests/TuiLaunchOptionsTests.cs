using Coda.Tui.Ui.Mode;

namespace Coda.Tui.Tests;

public sealed class TuiLaunchOptionsTests
{
    [Theory]
    [InlineData("--tui=auto", TuiPreference.Auto)]
    [InlineData("--tui=inline", TuiPreference.Inline)]
    [InlineData("--tui=fullscreen", TuiPreference.Fullscreen)]
    public void Parse_accepts_supported_tui_values(string arg, TuiPreference expected)
    {
        var parsed = TuiLaunchOptions.Parse([arg, "--continue"]);

        Assert.Null(parsed.Error);
        Assert.Equal(expected, parsed.Preference);
        Assert.False(parsed.Plain);
        Assert.Equal(["--continue"], parsed.RemainingArgs);
    }

    [Fact]
    public void Plain_overrides_tui_and_is_removed_from_session_args()
    {
        var parsed = TuiLaunchOptions.Parse(["--tui=fullscreen", "--plain", "--resume", "abc"]);

        Assert.Null(parsed.Error);
        Assert.True(parsed.Plain);
        Assert.Equal(["--resume", "abc"], parsed.RemainingArgs);
    }

    [Fact]
    public void Parse_rejects_unknown_tui_value()
    {
        var parsed = TuiLaunchOptions.Parse(["--tui=windowed"]);

        Assert.Equal("Invalid --tui value 'windowed'. Expected auto, inline, or fullscreen.", parsed.Error);
    }

    [Fact]
    public void Parse_accepts_explicit_mouse_disable()
    {
        var parsed = TuiLaunchOptions.Parse(["--no-mouse"]);

        Assert.True(parsed.MouseDisabled);
        Assert.Empty(parsed.RemainingArgs);
    }
}
