using Coda.Tui.Ui.Host;

namespace Coda.Tui.Tests;

/// <summary>
/// Covers the one-time startup guarantee: interactive startup (resume/fork seed, MCP connect, first-run
/// setup, initial snapshot publication) must run exactly once for the whole session, and every mode
/// attempt and fallback must await the SAME completion. A frame/actor fault in one mode therefore cannot
/// re-run startup's side effects, nor let a fallback mode re-enable submission before startup finished.
/// </summary>
public sealed class AsyncStartupGateTests
{
    [Fact]
    public async Task Runs_the_startup_only_once_across_repeated_calls()
    {
        var runs = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new AsyncStartupGate();
        Func<Task> factory = async () =>
        {
            Interlocked.Increment(ref runs);
            await release.Task;
        };

        var first = gate.RunOnceAsync(factory);
        var second = gate.RunOnceAsync(factory);

        // Both callers observe the same in-flight run; the factory ran once.
        Assert.Same(first, second);
        Assert.Equal(1, runs);

        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        // A caller arriving after completion still gets the original run, never a restart.
        var third = gate.RunOnceAsync(factory);
        Assert.Same(first, third);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Concurrent_callers_all_share_a_single_run()
    {
        var runs = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new AsyncStartupGate();
        Func<Task> factory = async () =>
        {
            Interlocked.Increment(ref runs);
            await release.Task;
        };

        // A handful of overlapping callers (kept small so the shared test run's thread pool is not
        // saturated) must all observe the single in-flight run.
        var results = new Task[8];
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            results[i] = gate.RunOnceAsync(factory);
        })));

        Assert.Equal(1, runs);
        Assert.All(results, r => Assert.Same(results[0], r));

        release.SetResult();
        await Task.WhenAll(results).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_faulted_startup_is_observed_by_all_callers_and_not_restarted()
    {
        var runs = 0;
        var gate = new AsyncStartupGate();
        Func<Task> factory = async () =>
        {
            Interlocked.Increment(ref runs);
            await Task.Yield();
            throw new InvalidOperationException("startup boom");
        };

        var first = gate.RunOnceAsync(factory);
        var second = gate.RunOnceAsync(factory);

        Assert.Same(first, second);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(1, runs);
    }
}
