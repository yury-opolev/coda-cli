using Coda.Tui.Commands;

namespace Coda.Tui.Tests;

public sealed class HelpCommandDetailTests
{
    [Fact]
    public async Task Help_with_command_name_shows_that_commands_usage()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        await new HelpCommand().ExecuteAsync(context, ["log"], CancellationToken.None);

        Assert.Contains("/log", console.Output);
        Assert.Contains("Usage", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Help_no_arg_lists_commands_with_footer_hint()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        await new HelpCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("/log", console.Output);
        Assert.Contains("--help", console.Output); // footer hint mentions per-command help
    }

    [Fact]
    public async Task Help_unknown_command_warns()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        await new HelpCommand().ExecuteAsync(context, ["definitelynotacommand"], CancellationToken.None);

        Assert.Contains("Unknown", console.Output, StringComparison.OrdinalIgnoreCase);
    }
}
