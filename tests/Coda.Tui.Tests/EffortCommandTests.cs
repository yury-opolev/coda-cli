using Coda.Tui.Commands;

namespace Coda.Tui.Tests;

public sealed class EffortCommandTests
{
    [Fact]
    public async Task Effort_with_no_args_reports_auto_by_default()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var command = new EffortCommand();
        await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("auto", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Null(context.Session.Effort);
    }

    [Fact]
    public async Task Effort_sets_level_on_session()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var command = new EffortCommand();
        await command.ExecuteAsync(context, ["high"], CancellationToken.None);

        Assert.Equal("high", context.Session.Effort);
        Assert.Contains("high", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Effort_auto_clears_the_level()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.Effort = "high";

        var command = new EffortCommand();
        await command.ExecuteAsync(context, ["auto"], CancellationToken.None);

        Assert.Null(context.Session.Effort);
    }

    [Fact]
    public async Task Effort_rejects_invalid_level()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var command = new EffortCommand();
        await command.ExecuteAsync(context, ["turbo"], CancellationToken.None);

        Assert.Null(context.Session.Effort);
        Assert.Contains("Invalid", console.Output, StringComparison.OrdinalIgnoreCase);
    }
}
