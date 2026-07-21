using Coda.Agent.Scheduling;
using Coda.Sdk;
using Engine.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

public sealed class SchedulingTests
{
    // ────────────────────────────────────────────────────────────────
    // CronExpression — TryParse
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("*/5 * * * *")]
    [InlineData("0 9 * * 1")]
    [InlineData("30 14 28 2 *")]
    [InlineData("0 0 * * *")]
    [InlineData("0,30 8-18 * * 1-5")]
    [InlineData("*/15 */6 1,15 * 0")]
    public void CronExpression_TryParse_valid(string expr)
    {
        var ok = CronExpression.TryParse(expr, out var cron, out var error);
        Assert.True(ok, $"Expected valid but got error: {error}");
        Assert.NotNull(cron);
        Assert.Null(error);
        Assert.Equal(expr, cron.Expression);
    }

    [Theory]
    [InlineData("* * * *")]            // 4 fields (too few)
    [InlineData("* * * * * *")]        // 6 fields (too many)
    [InlineData("60 * * * *")]         // minute out of range
    [InlineData("* 24 * * *")]         // hour out of range
    [InlineData("* * 0 * *")]          // dom out of range (< 1)
    [InlineData("* * 32 * *")]         // dom out of range (> 31)
    [InlineData("* * * 0 *")]          // month out of range (< 1)
    [InlineData("* * * 13 *")]         // month out of range (> 12)
    [InlineData("* * * * 7")]          // dow out of range (> 6)
    [InlineData("garbage")]            // not a cron expression
    [InlineData("abc * * * *")]        // non-numeric field
    [InlineData("*/0 * * * *")]        // step of zero
    [InlineData("5-3 * * * *")]        // inverted range
    public void CronExpression_TryParse_invalid(string expr)
    {
        var ok = CronExpression.TryParse(expr, out var cron, out var error);
        Assert.False(ok, $"Expected invalid but parsed successfully for: {expr}");
        Assert.Null(cron);
        Assert.NotNull(error);
    }

    // ────────────────────────────────────────────────────────────────
    // CronExpression — NextOccurrence
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void NextOccurrence_every5min_returns_next_boundary()
    {
        CronExpression.TryParse("*/5 * * * *", out var cron, out _);
        Assert.NotNull(cron);

        // 12:03 → next boundary is 12:05
        var after = new DateTime(2025, 1, 1, 12, 3, 0, DateTimeKind.Utc);
        var next = cron.NextOccurrence(after);
        Assert.Equal(new DateTime(2025, 1, 1, 12, 5, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void NextOccurrence_every5min_strictly_after_boundary()
    {
        CronExpression.TryParse("*/5 * * * *", out var cron, out _);
        Assert.NotNull(cron);

        // Exactly on a boundary → next is 5 minutes later (strictly after)
        var after = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var next = cron.NextOccurrence(after);
        Assert.Equal(new DateTime(2025, 1, 1, 12, 5, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void NextOccurrence_midnight_daily()
    {
        CronExpression.TryParse("0 0 * * *", out var cron, out _);
        Assert.NotNull(cron);

        // 2025-01-01 12:00 → next midnight is 2025-01-02 00:00
        var after = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var next = cron.NextOccurrence(after);
        Assert.Equal(new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void NextOccurrence_specific_time()
    {
        // "30 14 28 2 *" = 14:30 on Feb 28
        CronExpression.TryParse("30 14 28 2 *", out var cron, out _);
        Assert.NotNull(cron);

        var after = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var next = cron.NextOccurrence(after);
        Assert.Equal(new DateTime(2025, 2, 28, 14, 30, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void NextOccurrence_weekly_monday_9am()
    {
        // "0 9 * * 1" = Monday 9am
        CronExpression.TryParse("0 9 * * 1", out var cron, out _);
        Assert.NotNull(cron);

        // 2025-01-01 is a Wednesday; next Monday is 2025-01-06
        var after = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var next = cron.NextOccurrence(after);
        Assert.Equal(new DateTime(2025, 1, 6, 9, 0, 0, DateTimeKind.Utc), next);
    }

    // ────────────────────────────────────────────────────────────────
    // ScheduledTaskStore
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void ScheduledTaskStore_Add_assigns_id_and_computes_next_run()
    {
        var store = new ScheduledTaskStore();
        var now = new DateTime(2025, 1, 1, 12, 3, 0, DateTimeKind.Utc);
        var task = store.Add("*/5 * * * *", "do something", recurring: true, nowUtc: now);

        Assert.NotEmpty(task.Id);
        Assert.Equal("*/5 * * * *", task.Cron);
        Assert.Equal("do something", task.Prompt);
        Assert.Equal(ScheduleKind.Cron, task.Kind);
        // Next run should be 12:05
        Assert.Equal(new DateTimeOffset(new DateTime(2025, 1, 1, 12, 5, 0, DateTimeKind.Utc)), task.NextRunUtc);
        Assert.Single(store.Items);
    }

    [Fact]
    public void ScheduledTaskStore_Add_throws_on_invalid_cron()
    {
        var store = new ScheduledTaskStore();
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Throws<ArgumentException>(() => store.Add("garbage", "prompt", true, now));
    }

    [Fact]
    public void ScheduledTaskStore_Remove_removes_task()
    {
        var store = new ScheduledTaskStore();
        var now = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var task = store.Add("*/5 * * * *", "work", true, now);

        var removed = store.Remove(task.Id);
        Assert.True(removed);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void ScheduledTaskStore_Remove_returns_false_for_unknown_id()
    {
        var store = new ScheduledTaskStore();
        Assert.False(store.Remove("nonexistent"));
    }

    [Fact]
    public void ScheduledTaskStore_persistence_round_trip()
    {
        var persistPath = Path.Combine(Path.GetTempPath(), $"sched_{Guid.NewGuid():N}.json");
        try
        {
            var now = new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc);

            // Write
            var store1 = new ScheduledTaskStore(persistPath);
            var task = store1.Add("0 9 * * *", "daily task", recurring: true, nowUtc: now);

            // Read back from same path
            var store2 = new ScheduledTaskStore(persistPath);
            Assert.Single(store2.Items);
            Assert.Equal(task.Id, store2.Items[0].Id);
            Assert.Equal("0 9 * * *", store2.Items[0].Cron);
            Assert.Equal("daily task", store2.Items[0].Prompt);
        }
        finally
        {
            if (File.Exists(persistPath))
            {
                File.Delete(persistPath);
            }
        }
    }

    [Fact]
    public void ScheduledTaskStore_tolerates_corrupt_file()
    {
        var persistPath = Path.Combine(Path.GetTempPath(), $"sched_corrupt_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(persistPath, "this is not valid json {{{{");
            var store = new ScheduledTaskStore(persistPath);
            Assert.Empty(store.Items); // corrupt → empty, no exception
        }
        finally
        {
            if (File.Exists(persistPath))
            {
                File.Delete(persistPath);
            }
        }
    }

    [Fact]
    public void ScheduledTaskStore_persist_failure_is_swallowed_and_logged_at_debug()
    {
        // Make the persist path nest under a regular FILE, so Directory.CreateDirectory
        // (and thus the save) throws deterministically on every platform.
        var blocker = Path.Combine(Path.GetTempPath(), $"sched_block_{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "i am a file, not a directory");
        var persistPath = Path.Combine(blocker, "nested", "scheduled_tasks.json");
        try
        {
            var logger = new CapturingLogger();
            var store = new ScheduledTaskStore(persistPath, logger);
            var now = new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc);

            // Add triggers Save(); persistence must fail but NOT throw — the task still
            // lands in the in-memory store (swallow semantics intact).
            var task = store.Add("0 9 * * *", "daily task", recurring: true, nowUtc: now);
            Assert.Single(store.Items);
            Assert.Equal(task.Id, store.Items[0].Id);
            Assert.False(File.Exists(persistPath));

            // The silent data loss is now observable at Debug.
            var entry = Assert.Single(logger.Entries, e => e.Message.Contains("scheduled-task persistence failed"));
            Assert.Equal(LogLevel.Debug, entry.Level);
        }
        finally
        {
            if (File.Exists(blocker))
            {
                File.Delete(blocker);
            }
        }
    }

    [Fact]
    public void ScheduledTaskStore_Replace_updates_task_by_id()
    {
        var store = new ScheduledTaskStore();
        var now = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var task = store.Add("*/5 * * * *", "work", true, now);

        var updated = task with { NextRunUtc = new DateTimeOffset(new DateTime(2025, 1, 1, 12, 10, 0, DateTimeKind.Utc)) };
        store.Replace(updated);

        Assert.Single(store.Items);
        Assert.Equal(new DateTimeOffset(new DateTime(2025, 1, 1, 12, 10, 0, DateTimeKind.Utc)), store.Items[0].NextRunUtc);
    }

    // ────────────────────────────────────────────────────────────────
    // CronExpression — NextOccurrence with impossible date
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void NextOccurrence_throws_when_no_match_within_bound()
    {
        // "0 0 30 2 *" = midnight on Feb 30, which never exists.
        // TryParse accepts it (dom=30 is in [1,31], month=2 is in [1,12]).
        var ok = CronExpression.TryParse("0 0 30 2 *", out var cron, out var error);
        Assert.True(ok, $"Expected TryParse to succeed for syntactically-valid expression, got: {error}");
        Assert.NotNull(cron);

        var someUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Throws<InvalidOperationException>(() => cron!.NextOccurrence(someUtc));
    }

    [Fact]
    public void ScheduledTaskStore_Add_throws_InvalidOperationException_for_impossible_cron()
    {
        // "0 0 30 2 *" parses fine but NextOccurrence throws — Add propagates that.
        var store = new ScheduledTaskStore();
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Throws<InvalidOperationException>(() => store.Add("0 0 30 2 *", "never", true, now));
        Assert.Empty(store.Items);
    }

}
