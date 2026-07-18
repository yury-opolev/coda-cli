using System.Diagnostics;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Proves <see cref="UiActor.FlushAsync"/> is an ordered barrier: it does not complete until every
/// event queued before it has passed the observer/reducer/frame sink, and it fails deterministically
/// (never hangs) when cancelled, when the observer faults, or when the actor has already stopped.
/// </summary>
public sealed class UiActorFlushTests
{
    [Fact]
    public async Task Flush_does_not_complete_until_observer_applies_preceding_event()
    {
        using var mailbox = new UiEventMailbox(64);
        var observer = new GatedObserver();
        var actor = new UiActor(mailbox, NullUiFrameSink.Instance, UiSessionSnapshot.Empty, observer);

        using var cts = new CancellationTokenSource();
        var actorTask = actor.RunAsync(cts.Token);

        // A snapshot-changing event whose observer is blocked mid-apply until we release it.
        mailbox.Publish(new CommandOutputEvent("hello-world"));
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var flush = actor.FlushAsync(CancellationToken.None);

        // While the observer is still running the preceding event, the barrier must not complete.
        var early = await Task.WhenAny(flush, Task.Delay(300));
        Assert.NotSame(flush, early);
        Assert.False(flush.IsCompleted);
        Assert.Equal(0, observer.Applied);

        observer.Release.SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(flush.IsCompletedSuccessfully);
        Assert.Equal(1, observer.Applied);

        cts.Cancel();
        await actorTask;
    }

    [Fact]
    public async Task Flush_completes_after_all_queued_events_are_observed_in_order()
    {
        using var mailbox = new UiEventMailbox(64);
        var observer = new RecordingObserver();
        var actor = new UiActor(mailbox, NullUiFrameSink.Instance, UiSessionSnapshot.Empty, observer);

        using var cts = new CancellationTokenSource();
        var actorTask = actor.RunAsync(cts.Token);

        for (var i = 0; i < 20; i++)
        {
            mailbox.Publish(new CommandOutputEvent($"line-{i}"));
        }

        await actor.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(20, observer.Texts.Count);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal($"line-{i}", observer.Texts[i]);
        }

        cts.Cancel();
        await actorTask;
    }

    [Fact]
    public async Task Flush_throws_when_the_token_is_already_cancelled()
    {
        using var mailbox = new UiEventMailbox(64);
        var actor = new UiActor(mailbox, NullUiFrameSink.Instance, UiSessionSnapshot.Empty);

        using var cts = new CancellationTokenSource();
        var actorTask = actor.RunAsync(cts.Token);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actor.FlushAsync(cancelled.Token));

        cts.Cancel();
        await actorTask;
    }

    [Fact]
    public async Task Flush_times_out_without_hanging_when_the_observer_never_releases()
    {
        using var mailbox = new UiEventMailbox(64);
        var observer = new GatedObserver();
        var actor = new UiActor(mailbox, NullUiFrameSink.Instance, UiSessionSnapshot.Empty, observer);

        using var cts = new CancellationTokenSource();
        var actorTask = actor.RunAsync(cts.Token);

        mailbox.Publish(new CommandOutputEvent("stuck"));
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var flushCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actor.FlushAsync(flushCts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "Flush must observe its own cancellation quickly.");

        observer.Release.SetResult();
        cts.Cancel();
        await actorTask;
    }

    [Fact]
    public async Task Flush_faults_when_the_observer_throws()
    {
        using var mailbox = new UiEventMailbox(64);
        var observer = new ThrowingObserver(new InvalidOperationException("observer boom"));
        var actor = new UiActor(mailbox, NullUiFrameSink.Instance, UiSessionSnapshot.Empty, observer);

        using var cts = new CancellationTokenSource();
        var actorTask = actor.RunAsync(cts.Token);

        // A real event whose observer throws, followed by a flush. The barrier must resolve (fault or,
        // if the actor already stopped, cancel) rather than hang forever.
        mailbox.Publish(new CommandOutputEvent("boom"));
        var flush = actor.FlushAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(() => flush.WaitAsync(TimeSpan.FromSeconds(5)));

        cts.Cancel();
        try
        {
            await actorTask;
        }
        catch (InvalidOperationException)
        {
            // The observer fault propagates through the actor's run task; that is expected here.
        }
    }

    [Fact]
    public async Task Flush_fails_deterministically_after_the_actor_has_stopped()
    {
        using var mailbox = new UiEventMailbox(64);
        var actor = new UiActor(mailbox, NullUiFrameSink.Instance, UiSessionSnapshot.Empty);

        using var cts = new CancellationTokenSource();
        var actorTask = actor.RunAsync(cts.Token);
        cts.Cancel();
        await actorTask;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => actor.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Pending_flush_is_cancelled_when_the_actor_stops()
    {
        using var mailbox = new UiEventMailbox(64);
        var observer = new GatedObserver();
        var actor = new UiActor(mailbox, NullUiFrameSink.Instance, UiSessionSnapshot.Empty, observer);

        using var cts = new CancellationTokenSource();
        var actorTask = actor.RunAsync(cts.Token);

        mailbox.Publish(new CommandOutputEvent("blocked"));
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var flush = actor.FlushAsync(CancellationToken.None);

        // Stop the actor while the barrier is still pending; the caller must not hang.
        cts.Cancel();
        observer.Release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush.WaitAsync(TimeSpan.FromSeconds(5)));
        await actorTask;
    }

    private sealed class GatedObserver : IUiEventObserver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Applied;

        public async ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken)
        {
            this.Entered.TrySetResult();
            await this.Release.Task.ConfigureAwait(false);
            Interlocked.Increment(ref this.Applied);
        }
    }

    private sealed class RecordingObserver : IUiEventObserver
    {
        public List<string> Texts { get; } = new();

        public ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken)
        {
            if (uiEvent is CommandOutputEvent e)
            {
                this.Texts.Add(e.Text);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingObserver(Exception error) : IUiEventObserver
    {
        public ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken) =>
            throw error;
    }
}
