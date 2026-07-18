using Coda.Tui;

namespace Coda.Tui.Tests;

/// <summary>
/// The plain-text, no-side-effect commands the executable handles before
/// starting the interactive TUI: <c>--version</c> and <c>--help</c>. These keep
/// a published <c>coda.exe</c> / global tool smoke-testable without auth.
/// </summary>
public sealed class ImmediateCliTests
{
    private static (int? code, string output) Run(params string[] args)
    {
        var writer = new StringWriter();
        var code = ImmediateCli.TryHandle(args, writer);
        return (code, writer.ToString());
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void Version_flag_prints_version_and_exits_zero(string flag)
    {
        var (code, output) = Run(flag);

        Assert.Equal(0, code);
        Assert.Contains(Branding.Version, output);
        Assert.Contains(Branding.ProductName, output);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_flag_prints_usage_and_exits_zero(string flag)
    {
        var (code, output) = Run(flag);

        Assert.Equal(0, code);
        Assert.Contains(Branding.CliName, output);
        Assert.Contains("run", output); // the headless subcommand is documented
    }

    [Fact]
    public void Help_documents_tui_and_plain_flags()
    {
        var (_, output) = Run("--help");

        Assert.Contains("--tui=auto|inline|fullscreen", output);
        Assert.Contains("--plain", output);
        Assert.Contains("--no-mouse", output);
    }

    [Fact]
    public void Help_says_auto_defaults_to_fullscreen_and_inline_is_optional()
    {
        var (_, output) = Run("--help");

        Assert.Contains("full-screen", output);
        Assert.Contains("optional", output);
    }

    [Fact]
    public void Help_documents_the_warm_ember_interaction_model()
    {
        var (_, output) = Run("--help");

        // Composer editing and submission.
        Assert.Contains("Enter", output);
        Assert.Contains("Ctrl+J", output);
        Assert.Contains("newline", output);

        // Visual arrow movement plus history at the edges.
        Assert.Contains("Up / Down", output);
        Assert.Contains("history", output);

        // Double-Esc interrupts the active turn.
        Assert.Contains("Esc twice", output);
        Assert.Contains("interrupt the active turn", output);

        // Ctrl+C copies a selection and only exits on a second press.
        Assert.Contains("Ctrl+C", output);
        Assert.Contains("selection", output);
        Assert.Contains("press twice to exit", output);

        // Selection is made by dragging or holding Shift.
        Assert.Contains("drag", output);
        Assert.Contains("Shift", output);

        // Exit is via /exit; F2 still toggles the host model.
        Assert.Contains("/exit", output);
        Assert.Contains("F2", output);
    }

    [Fact]
    public void Help_does_not_bind_ctrl_d()
    {
        var (_, output) = Run("--help");

        // Warm Ember removes the Ctrl+D exit binding; the help must not advertise it.
        Assert.DoesNotContain("Ctrl+D", output);
        Assert.DoesNotContain("Ctrl-D", output);
    }

    [Fact]
    public void No_args_returns_null_to_continue_to_the_interactive_tui()
    {
        var (code, _) = Run();

        Assert.Null(code);
    }

    [Fact]
    public void Unrecognized_args_return_null_to_continue()
    {
        var (code, _) = Run("--something-else");

        Assert.Null(code);
    }
}
