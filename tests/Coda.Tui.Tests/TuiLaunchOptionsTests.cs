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

    [Fact]
    public void Parse_extracts_inline_prompt_without_reordering_session_arguments()
    {
        var parsed = TuiLaunchOptions.Parse(
            ["--resume", "abc", "--system-prompt", "exact", "--plain", "--fork", "def", "--no-mouse"]);

        Assert.Null(parsed.Error);
        Assert.Equal("exact", Assert.IsType<Coda.Tui.SystemPromptSource.Inline>(parsed.SystemPromptSource).Text);
        Assert.True(parsed.Plain);
        Assert.True(parsed.MouseDisabled);
        Assert.Equal(["--resume", "abc", "--fork", "def"], parsed.RemainingArgs);
    }

    [Fact]
    public void Parse_extracts_file_prompt_without_reordering_session_arguments()
    {
        var parsed = TuiLaunchOptions.Parse(
            ["--fork", "def", "--system-prompt-file", "prompt.txt", "--resume", "abc", "--tui=inline"]);

        Assert.Null(parsed.Error);
        Assert.Equal("prompt.txt", Assert.IsType<Coda.Tui.SystemPromptSource.FilePath>(parsed.SystemPromptSource).Path);
        Assert.Equal(TuiPreference.Inline, parsed.Preference);
        Assert.Equal(["--fork", "def", "--resume", "abc"], parsed.RemainingArgs);
    }

    [Theory]
    [InlineData("--system-prompt", "--system-prompt requires a value.")]
    [InlineData("--system-prompt-file", "--system-prompt-file requires a value.")]
    [InlineData("--system-prompt=exact", "System prompt options require separate arguments for the flag and value.")]
    [InlineData("--system-prompt-file=prompt.txt", "System prompt options require separate arguments for the flag and value.")]
    public void Parse_rejects_missing_or_equals_prompt_sources(string argument, string expectedError)
    {
        var parsed = TuiLaunchOptions.Parse([argument]);

        Assert.Equal(expectedError, parsed.Error);
    }

    [Fact]
    public void Parse_rejects_duplicate_prompt_sources()
    {
        var parsed = TuiLaunchOptions.Parse(
            ["--system-prompt", "one", "--system-prompt-file", "prompt.txt"]);

        Assert.Equal("Specify only one of --system-prompt or --system-prompt-file, once.", parsed.Error);
    }

    [Fact]
    public void Parse_preserves_exact_dash_prefixed_prompt_value()
    {
        var parsed = TuiLaunchOptions.Parse(["--system-prompt", "--exact-value"]);

        Assert.Null(parsed.Error);
        Assert.Equal("--exact-value", Assert.IsType<Coda.Tui.SystemPromptSource.Inline>(parsed.SystemPromptSource).Text);
    }
}
