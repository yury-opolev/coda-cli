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
