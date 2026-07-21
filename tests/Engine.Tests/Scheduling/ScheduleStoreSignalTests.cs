using Coda.Agent.Scheduling;

namespace Engine.Tests.Scheduling;

/// <summary>
/// Task 2 version/change-signal contract: monotonic versioning, race-free
/// <see cref="ScheduledTaskStore.WaitForChangeAsync"/>, cancellation, and concurrency safety.
/// </summary>
public sealed class ScheduleStoreSignalTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WaitForChange_completes_on_Add()
    {
        var store = new ScheduledTaskStore();
        var version = store.GetSnapshot().Version;

        var wait = store.WaitForChangeAsync(version);
        Assert.False(wait.IsCompleted);

        store.Add(IntervalDraft(), Now);
        await wait.WaitAsync(Timeout);

        Assert.True(store.GetSnapshot().Version > version);
    }

    [Fact]
    public async Task WaitForChange_completes_on_Remove()
    {
        var store = new ScheduledTaskStore();
        var task = store.Add(IntervalDraft(), Now);
        var version = store.GetSnapshot().Version;

        var wait = store.WaitForChangeAsync(version);
        Assert.False(wait.IsCompleted);

        Assert.True(store.Remove(task.Id));
        await wait.WaitAsync(Timeout);
    }

    [Fact]
    public async Task WaitForChange_completes_on_Replace()
    {
        var store = new ScheduledTaskStore();
        var task = store.Add(IntervalDraft(), Now);
        var version = store.GetSnapshot().Version;

        var wait = store.WaitForChangeAsync(version);
        Assert.False(wait.IsCompleted);

        Assert.True(store.Replace(task with { Prompt = "changed" }));
        await wait.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Unknown_Remove_and_Replace_do_not_change_version_or_signal()
    {
        var store = new ScheduledTaskStore();
        store.Add(IntervalDraft(), Now);
        var version = store.GetSnapshot().Version;

        var wait = store.WaitForChangeAsync(version);

        Assert.False(store.Remove("does-not-exist"));
        Assert.False(store.Replace(Unknown()));

        Assert.Equal(version, store.GetSnapshot().Version);

        // No signal should fire for the unknown mutations.
        await Task.Delay(150);
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public void Mutation_between_snapshot_and_wait_is_not_missed()
    {
        var store = new ScheduledTaskStore();
        var version = store.GetSnapshot().Version;

        // A mutation lands AFTER the snapshot but BEFORE the wait is registered.
        store.Add(IntervalDraft(), Now);

        var wait = store.WaitForChangeAsync(version);

        // The store already advanced past the observed version, so the wait must be
        // satisfied immediately rather than blocking forever.
        Assert.True(wait.IsCompleted);
    }

    [Fact]
    public async Task WaitForChange_honours_cancellation()
    {
        var store = new ScheduledTaskStore();
        var version = store.GetSnapshot().Version;

        using var cts = new CancellationTokenSource();
        var wait = store.WaitForChangeAsync(version, cts.Token);
        Assert.False(wait.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task Version_is_monotonic_across_mutations()
    {
        var store = new ScheduledTaskStore();
        var v0 = store.GetSnapshot().Version;

        var a = store.Add(IntervalDraft(), Now);
        var v1 = store.GetSnapshot().Version;
        Assert.True(v1 > v0);

        store.Replace(a with { Prompt = "x" });
        var v2 = store.GetSnapshot().Version;
        Assert.True(v2 > v1);

        store.Remove(a.Id);
        var v3 = store.GetSnapshot().Version;
        Assert.True(v3 > v2);

        // Unknown mutation must not advance the version.
        store.Remove("nope");
        Assert.Equal(v3, store.GetSnapshot().Version);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Concurrent_waiters_and_mutations_stay_deterministic_without_deadlock()
    {
        var store = new ScheduledTaskStore();
        const int mutators = 4;
        const int perMutator = 75;
        const long target = mutators * perMutator;

        async Task Follow()
        {
            var observed = store.GetSnapshot().Version;
            while (observed < target)
            {
                await store.WaitForChangeAsync(observed).WaitAsync(TimeSpan.FromSeconds(30));
                observed = store.GetSnapshot().Version;
            }
        }

        var waiters = Enumerable.Range(0, 8).Select(_ => Task.Run(Follow)).ToArray();

        var writers = Enumerable.Range(0, mutators).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perMutator; i++)
            {
                store.Add(IntervalDraft(), Now);
            }
        })).ToArray();

        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(60));
        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(60));

        var snap = store.GetSnapshot();
        Assert.Equal(target, snap.Version);
        Assert.Equal((int)target, snap.Items.Count);
    }

    private static ScheduleDefinitionDraft IntervalDraft() =>
        new(null, ScheduleKind.Interval, "interval prompt", TimeSpan.FromMinutes(5), null, null, "UTC", Now + TimeSpan.FromMinutes(5));

    private static ScheduledTask Unknown() =>
        new(
            ScheduledTask.CurrentSchemaVersion,
            "unknown-0001",
            Name: null,
            Kind: ScheduleKind.Interval,
            Prompt: "unknown",
            Interval: TimeSpan.FromMinutes(5),
            AtUtc: null,
            Cron: null,
            TimeZoneId: "UTC",
            NextRunUtc: Now,
            CreatedAtUtc: Now,
            UpdatedAtUtc: Now,
            LastTerminalOutcome: null);
}
