using Coda.Tui.Commands;

namespace Coda.Tui.Tests;

public sealed class GoalCommandTests
{
    [Fact]
    public async Task SetGoal_sets_session_goal()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        var command = new GoalCommand();

        await command.ExecuteAsync(context, ["ship", "it"], CancellationToken.None);

        Assert.Equal("ship it", context.Session.Goal);
    }

    [Fact]
    public async Task GoalOff_clears_goal()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.Goal = "ship it";
        context.Session.GoalMaxDuration = TimeSpan.FromMinutes(30);
        context.Session.GoalMaxContinuations = 50;
        var command = new GoalCommand();

        await command.ExecuteAsync(context, ["off"], CancellationToken.None);

        Assert.Null(context.Session.Goal);
        Assert.Null(context.Session.GoalMaxDuration);
        Assert.Null(context.Session.GoalMaxContinuations);
    }

    [Fact]
    public async Task GoalClear_clears_goal()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.Goal = "something";
        var command = new GoalCommand();

        await command.ExecuteAsync(context, ["clear"], CancellationToken.None);

        Assert.Null(context.Session.Goal);
    }

    [Fact]
    public async Task SetGoal_with_timeout_sets_duration()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        var command = new GoalCommand();

        await command.ExecuteAsync(context, ["--timeout", "10m", "fix", "bug"], CancellationToken.None);

        Assert.Equal("fix bug", context.Session.Goal);
        Assert.Equal(TimeSpan.FromMinutes(10), context.Session.GoalMaxDuration);
    }

    [Fact]
    public async Task SetGoal_with_max_turns_sets_continuations()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        var command = new GoalCommand();

        await command.ExecuteAsync(context, ["--max-turns", "50", "do", "x"], CancellationToken.None);

        Assert.Equal("do x", context.Session.Goal);
        Assert.Equal(50, context.Session.GoalMaxContinuations);
    }

    [Fact]
    public async Task SetGoal_with_bogus_timeout_does_not_set_goal()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        var command = new GoalCommand();

        await command.ExecuteAsync(context, ["--timeout", "bogus", "x"], CancellationToken.None);

        Assert.Null(context.Session.Goal);
    }

    [Fact]
    public async Task NoArgs_with_no_goal_prints_no_goal_message()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        var command = new GoalCommand();

        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("No goal", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoArgs_with_active_goal_shows_goal_text()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.Goal = "ship it";
        var command = new GoalCommand();

        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("ship it", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetGoal_without_text_warns()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        var command = new GoalCommand();

        await command.ExecuteAsync(context, ["--timeout", "10m"], CancellationToken.None);

        Assert.Null(context.Session.Goal);
        Assert.Contains("goal", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetGoal_confirms_text_in_output()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        var command = new GoalCommand();

        await command.ExecuteAsync(context, ["ship", "the", "feature"], CancellationToken.None);

        Assert.Equal("ship the feature", context.Session.Goal);
        Assert.Contains("ship the feature", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GoalOff_with_trailing_text_sets_goal_not_clear()
    {
        // "off"/"clear"/etc. only clear when the sole argument; multi-word input is goal text.
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        var command = new GoalCommand();

        await command.ExecuteAsync(context, ["off", "the", "lights"], CancellationToken.None);

        Assert.Equal("off the lights", context.Session.Goal);
    }

    [Fact]
    public async Task SetGoal_with_bogus_timeout_warns()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        var command = new GoalCommand();

        await command.ExecuteAsync(context, ["--timeout", "bogus", "x"], CancellationToken.None);

        Assert.Null(context.Session.Goal);
        Assert.Contains("Invalid duration", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetGoal_with_bogus_max_turns_does_not_set_goal_and_warns()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        var command = new GoalCommand();

        await command.ExecuteAsync(context, ["--max-turns", "abc", "do", "x"], CancellationToken.None);

        Assert.Null(context.Session.Goal);
        Assert.Contains("turn count", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetGoal_with_both_flags_sets_both_overrides()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        var command = new GoalCommand();

        await command.ExecuteAsync(context, ["--timeout", "2h", "--max-turns", "100", "green", "build"], CancellationToken.None);

        Assert.Equal("green build", context.Session.Goal);
        Assert.Equal(TimeSpan.FromHours(2), context.Session.GoalMaxDuration);
        Assert.Equal(100, context.Session.GoalMaxContinuations);
    }
}
