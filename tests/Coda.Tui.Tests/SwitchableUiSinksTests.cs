using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Covers the actor-fault fallback wiring: the shared <see cref="UiActor"/> keeps one switchable
/// frame sink and event observer whose targets the interactive host swaps per mode. A frame-sink fault
/// must clear the target (so the shared actor keeps draining for the fallback mode) and route the fault
/// to the current mode's handler exactly once, while cancellation propagates untouched. An observer
/// fault must never fault the shared actor, but cancellation still propagates.
/// </summary>
public sealed class SwitchableUiSinksTests
{
    [Fact]
    public async Task Frame_sink_applies_snapshots_to_the_current_target()
    {
        var target = new RecordingFrameSink();
        var sink = new SwitchableUiFrameSink();
        sink.Set(target, _ => { });

        await sink.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);

        Assert.Equal(1, target.Applied);
    }

    [Fact]
    public async Task Frame_sink_with_no_target_is_a_no_op()
    {
        var sink = new SwitchableUiFrameSink();

        // No target set at all, then explicitly cleared: neither call may throw.
        await sink.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
        sink.Set(null, null);
        await sink.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
    }

    [Fact]
    public async Task Frame_sink_fault_clears_the_target_and_routes_to_the_handler_once()
    {
        var boom = new InvalidOperationException("frame fault");
        var target = new ThrowingFrameSink(boom);
        Exception? routed = null;
        var faults = 0;
        var sink = new SwitchableUiFrameSink();
        sink.Set(target, ex =>
        {
            routed = ex;
            faults++;
        });

        // The first apply faults: the exception is routed once and the target is cleared so the shared
        // actor survives (it becomes a no-op sink until the fallback mode re-points it).
        await sink.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
        Assert.Same(boom, routed);
        Assert.Equal(1, faults);

        // A second apply is a no-op: the faulted target is not re-invoked and no second fault is routed.
        await sink.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
        Assert.Equal(1, faults);
        Assert.Equal(1, target.Attempts);
    }

    [Fact]
    public async Task Frame_sink_rethrows_cancellation_without_routing_a_fault()
    {
        var target = new ThrowingFrameSink(new OperationCanceledException());
        var faults = 0;
        var sink = new SwitchableUiFrameSink();
        sink.Set(target, _ => faults++);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await sink.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None));

        // Cancellation is host shutdown, not a sink fault: the target stays and no fallback is triggered.
        Assert.Equal(0, faults);
    }

    [Fact]
    public async Task Observer_forwards_events_to_the_current_target()
    {
        var target = new RecordingObserver();
        var observer = new SwitchableUiEventObserver();
        observer.Set(target);

        await observer.ApplyEventAsync(new WarningEvent("hi"), CancellationToken.None);

        Assert.Equal(1, target.Applied);
    }

    [Fact]
    public async Task Observer_swallows_a_non_cancellation_fault()
    {
        var observer = new SwitchableUiEventObserver();
        observer.Set(new ThrowingObserver(new InvalidOperationException("observer fault")));

        // A single mode observer failure must never fault the shared actor.
        await observer.ApplyEventAsync(new WarningEvent("hi"), CancellationToken.None);
    }

    [Fact]
    public async Task Observer_rethrows_cancellation()
    {
        var observer = new SwitchableUiEventObserver();
        observer.Set(new ThrowingObserver(new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await observer.ApplyEventAsync(new WarningEvent("hi"), CancellationToken.None));
    }

    [Fact]
    public async Task Observer_with_no_target_is_a_no_op()
    {
        var observer = new SwitchableUiEventObserver();

        await observer.ApplyEventAsync(new WarningEvent("hi"), CancellationToken.None);
        observer.Set(null);
        await observer.ApplyEventAsync(new WarningEvent("hi"), CancellationToken.None);
    }

    private sealed class RecordingFrameSink : IUiFrameSink
    {
        public int Applied { get; private set; }

        public ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            this.Applied++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingFrameSink(Exception error) : IUiFrameSink
    {
        public int Attempts { get; private set; }

        public ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            this.Attempts++;
            throw error;
        }
    }

    private sealed class RecordingObserver : IUiEventObserver
    {
        public int Applied { get; private set; }

        public ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken)
        {
            this.Applied++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingObserver(Exception error) : IUiEventObserver
    {
        public ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken) => throw error;
    }
}
