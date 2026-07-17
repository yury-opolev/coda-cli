using Coda.Sdk;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Verifies the turn-scoped caches that sit in front of expensive probes: the context-window
/// analyzer (<see cref="ContextSnapshotCache"/>) and the git working-tree probe
/// (<see cref="GitStatusCache"/>). Both memoize until explicitly invalidated at the turn boundary
/// and coalesce concurrent callers onto a single in-flight task.
/// </summary>
public sealed class ContextSnapshotCacheTests
{
    private static ContextReport Report(int used = 100, string model = "m") => new()
    {
        Model = model,
        MaxTokens = 1000,
        Categories = [],
        UsedTokens = used,
        IsExact = true,
        MessageCount = 1,
    };

    [Fact]
    public async Task ContextSnapshotCache_calls_analyzer_once_until_invalidated()
    {
        var calls = 0;
        var cache = new ContextSnapshotCache(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Report());
        });

        Assert.Null(cache.Current);

        var first = await cache.GetAsync();
        var second = await cache.GetAsync();

        Assert.Equal(1, calls);
        Assert.Same(first, second);
        Assert.Same(first, cache.Current);

        cache.InvalidateAfterTurn();
        await cache.GetAsync();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ContextSnapshotCache_force_refreshes_even_without_invalidation()
    {
        var calls = 0;
        var cache = new ContextSnapshotCache(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Report());
        });

        await cache.GetAsync();
        await cache.GetAsync(force: true);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ContextSnapshotCache_coalesces_concurrent_callers_onto_one_task()
    {
        var calls = 0;
        var gate = new TaskCompletionSource<ContextReport>();
        var cache = new ContextSnapshotCache(_ =>
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        });

        var a = cache.GetAsync();
        var b = cache.GetAsync();

        Assert.False(a.IsCompleted);
        Assert.Equal(1, calls);

        var report = Report();
        gate.SetResult(report);

        Assert.Same(report, await a);
        Assert.Same(report, await b);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ContextSnapshotCache_caller_cancellation_does_not_corrupt_shared_state()
    {
        var gate = new TaskCompletionSource<ContextReport>();
        var started = 0;
        var cache = new ContextSnapshotCache(_ =>
        {
            Interlocked.Increment(ref started);
            return gate.Task;
        });

        using var cts = new CancellationTokenSource();
        var cancelled = cache.GetAsync(cancellationToken: cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        // The shared analyzer keeps running; a fresh caller still observes the completed report.
        var report = Report();
        gate.SetResult(report);

        var recovered = await cache.GetAsync();
        Assert.Same(report, recovered);
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task GitStatusCache_probes_once_per_directory_until_invalidated()
    {
        var calls = new Dictionary<string, int>();
        var cache = new GitStatusCache((dir, _) =>
        {
            lock (calls)
            {
                calls[dir] = calls.TryGetValue(dir, out var n) ? n + 1 : 1;
            }

            return Task.FromResult(new GitStatus(dir, Dirty: true));
        });

        var a1 = await cache.GetAsync("/repo/a");
        var a2 = await cache.GetAsync("/repo/a");
        var b1 = await cache.GetAsync("/repo/b");

        Assert.Equal(new GitStatus("/repo/a", true), a1);
        Assert.Equal(a1, a2);
        Assert.Equal(new GitStatus("/repo/b", true), b1);
        Assert.Equal(1, calls["/repo/a"]);
        Assert.Equal(1, calls["/repo/b"]);

        cache.InvalidateAfterTurn();
        await cache.GetAsync("/repo/a");

        Assert.Equal(2, calls["/repo/a"]);
    }

    [Fact]
    public async Task GitStatusCache_does_not_swallow_caller_cancellation()
    {
        var gate = new TaskCompletionSource<GitStatus>();
        var cache = new GitStatusCache((_, _) => gate.Task);

        using var cts = new CancellationTokenSource();
        var pending = cache.GetAsync("/repo", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
