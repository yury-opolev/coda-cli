using Coda.Tui.Commands;
using LlmClient;

namespace Coda.Tui.Tests;

// ── Phase 2 Item 2 — /effort warns about cache rebuild mid-session ────────────

public sealed class EffortCacheRebuildWarningTests
{
    [Fact]
    public async Task Effort_change_mid_session_warns_about_cache_rebuild()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        // Simulate a non-empty conversation history (mid-session).
        context.Session.History.Add(ChatMessage.UserText("hello"));
        context.Session.Effort = null; // current is "auto"

        var command = new EffortCommand((_, _, _) => string.Empty);
        await command.ExecuteAsync(context, ["high"], CancellationToken.None);

        // The warning about cache rebuild must appear.
        Assert.Contains("cache", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rebuilt", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Effort_change_on_empty_history_does_not_warn_about_cache()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        // No history — first session turn.
        Assert.Empty(context.Session.History);

        var command = new EffortCommand((_, _, _) => string.Empty);
        await command.ExecuteAsync(context, ["high"], CancellationToken.None);

        // No cache-rebuild warning on a fresh session.
        Assert.DoesNotContain("rebuilt", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Effort_same_value_does_not_warn_about_cache()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.History.Add(ChatMessage.UserText("hi"));
        context.Session.Effort = "high"; // already high

        var command = new EffortCommand((_, _, _) => string.Empty);
        await command.ExecuteAsync(context, ["high"], CancellationToken.None);

        // No cache-rebuild warning when the value didn't change.
        Assert.DoesNotContain("rebuilt", console.Output, StringComparison.OrdinalIgnoreCase);
    }
}

// ── Phase 3 — /cost shows cache savings ──────────────────────────────────────

public sealed class CostCacheSavingsTests
{
    [Fact]
    public async Task Cost_shows_savings_when_cache_is_active()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        // Usage with substantial cache reads: savings should be visible.
        context.Session.SessionUsage = new TokenUsage(
            InputTokens: 200,
            OutputTokens: 100,
            CacheReadTokens: 5000,   // large cache read portion
            CacheWrite5mTokens: 500);

        var command = new CostCommand();
        await command.ExecuteAsync(context, System.Array.Empty<string>(), CancellationToken.None);

        // The output must mention "savings".
        Assert.Contains("savings", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cost_does_not_show_savings_when_no_cache_activity()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.SessionUsage = new TokenUsage(1000, 500);  // no cache

        var command = new CostCommand();
        await command.ExecuteAsync(context, System.Array.Empty<string>(), CancellationToken.None);

        // No savings line when there's no cache activity.
        Assert.DoesNotContain("savings", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cost_does_not_show_misleading_savings_when_no_usage()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        // SessionUsage defaults to Zero.

        var command = new CostCommand();
        await command.ExecuteAsync(context, System.Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain("savings", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$", console.Output, StringComparison.OrdinalIgnoreCase);
    }
}
