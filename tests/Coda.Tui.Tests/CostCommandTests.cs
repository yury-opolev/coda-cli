using Coda.Tui.Commands;
using Coda.Tui.Repl;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class CostCommandTests
{
    [Fact]
    public async Task Cost_with_no_usage_says_no_usage_recorded()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        // SessionUsage defaults to Zero

        var command = new CostCommand();
        var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("No usage recorded", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cost_with_usage_shows_token_counts()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.SessionUsage = new TokenUsage(1500, 300);

        var command = new CostCommand();
        var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("1,500", console.Output);
        Assert.Contains("300", console.Output);
    }

    [Fact]
    public async Task Cost_with_usage_shows_dollar_sign()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.SessionUsage = new TokenUsage(1_000_000, 500_000);

        var command = new CostCommand();
        await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("$", console.Output);
    }

    [Fact]
    public async Task Cost_shows_total_tokens()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.SessionUsage = new TokenUsage(200, 100);

        var command = new CostCommand();
        await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("300", console.Output); // total = 200 + 100
    }

    [Fact]
    public async Task Cost_returns_continue()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.SessionUsage = new TokenUsage(100, 50);

        var command = new CostCommand();
        var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.False(result.ShouldExit);
    }
}

public sealed class ClearCommandUsageTests
{
    [Fact]
    public async Task Clear_resets_SessionUsage_to_zero()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.SessionUsage = new TokenUsage(500, 200);

        var command = new ClearCommand();
        await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(TokenUsage.Zero, context.Session.SessionUsage);
    }

    [Fact]
    public async Task Clear_also_clears_history()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.History.Add(LlmClient.ChatMessage.UserText("hello"));
        context.Session.SessionUsage = new TokenUsage(100, 50);

        var command = new ClearCommand();
        await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Empty(context.Session.History);
        Assert.Equal(TokenUsage.Zero, context.Session.SessionUsage);
    }
}
