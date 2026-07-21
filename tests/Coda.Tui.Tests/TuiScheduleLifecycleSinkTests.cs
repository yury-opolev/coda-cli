using Coda.Sdk;
using Coda.Sdk.Scheduling;
using Coda.Tui.Agent;
using Coda.Tui.Ui.Events;
using LlmClient;

namespace Coda.Tui.Tests;

/// <summary>
/// Task 8 coverage for <see cref="TuiScheduleLifecycleSink"/>: it must publish a typed lifecycle
/// notice followed IMMEDIATELY by a fresh runtime snapshot (pulled at publish time), and be safe on
/// the background firing path — swallowing only the shutdown <see cref="ObjectDisposedException"/>
/// while letting unexpected faults surface.
/// </summary>
public sealed class TuiScheduleLifecycleSinkTests
{
    private static SessionRuntimeSnapshot Snapshot() =>
        new("s1", new TokenUsage(0, 0), null, [], [], [], [], []);

    [Fact]
    public async Task Sink_publishes_lifecycle_then_a_fresh_snapshot_in_order()
    {
        var events = new RecordingUiEvents();
        var snapshot = Snapshot();
        var pulls = 0;
        var sink = new TuiScheduleLifecycleSink(events, () => { pulls++; return snapshot; });

        await sink.PublishAsync(new ScheduleLifecycleEvent(
            "def-1", "Nightly", "task-9", ScheduleLifecycleKind.Started, DateTimeOffset.UnixEpoch, null));

        Assert.Collection(
            events.Events,
            e =>
            {
                var lifecycle = Assert.IsType<ScheduleLifecycleChangedEvent>(e);
                Assert.Equal(ScheduleLifecycleKind.Started, lifecycle.Lifecycle.Kind);
                Assert.Equal("task-9", lifecycle.Lifecycle.TaskId);
            },
            e =>
            {
                var runtime = Assert.IsType<SessionRuntimeChangedEvent>(e);
                Assert.Same(snapshot, runtime.Snapshot);
            });

        // The snapshot is pulled fresh at publish time, never captured at construction.
        Assert.Equal(1, pulls);
    }

    [Fact]
    public async Task Sink_swallows_ObjectDisposedException_during_shutdown()
    {
        var sink = new TuiScheduleLifecycleSink(new ThrowingPublisher(new ObjectDisposedException("mailbox")), Snapshot);

        // No throw: a lifecycle event arriving while the UI mailbox is disposed is dropped safely.
        await sink.PublishAsync(new ScheduleLifecycleEvent(
            "def", "n", "t", ScheduleLifecycleKind.Completed, DateTimeOffset.UnixEpoch, null));
    }

    [Fact]
    public async Task Sink_does_not_swallow_unexpected_publish_faults()
    {
        var sink = new TuiScheduleLifecycleSink(new ThrowingPublisher(new InvalidOperationException("boom")), Snapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sink.PublishAsync(new ScheduleLifecycleEvent(
                "def", null, "t", ScheduleLifecycleKind.Failed, DateTimeOffset.UnixEpoch, "err")));
    }

    private sealed class ThrowingPublisher(Exception fault) : IUiEventPublisher
    {
        public void Publish(UiEvent uiEvent) => throw fault;
    }
}
