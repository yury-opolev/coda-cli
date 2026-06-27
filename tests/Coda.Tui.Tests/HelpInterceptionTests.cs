using Coda.Tui;
using Coda.Tui.Repl;

namespace Coda.Tui.Tests;

public sealed class HelpInterceptionTests
{
    [Theory]
    [InlineData("--help", true)]
    [InlineData("-h", true)]
    [InlineData("help", true)]
    [InlineData("HELP", true)]
    [InlineData("debug", false)]
    [InlineData("", false)]
    public void IsHelpToken_detects_help_requests(string token, bool expected)
    {
        Assert.Equal(expected, HelpToken.IsHelpToken(token));
    }

    [Fact]
    public async Task Dispatching_command_with_help_flag_renders_help_and_skips_execution()
    {
        var (app, _, console, _) = TestAppBuilder.BuildApp();

        var result = await app.DispatchAsync(CommandParser.Parse("/log --help"), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("Usage", console.Output, StringComparison.OrdinalIgnoreCase); // help was rendered
        Assert.DoesNotContain("Invalid", console.Output, StringComparison.OrdinalIgnoreCase); // ExecuteAsync did NOT run
    }
}
