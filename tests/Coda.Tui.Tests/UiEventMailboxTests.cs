using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class UiEventMailboxTests
{
    [Fact]
    public async Task Assistant_deltas_coalesce_into_a_single_concatenated_read()
    {
        using var mailbox = new UiEventMailbox(capacity: 4);

        for (var i = 0; i < 100; i++)
        {
            mailbox.Publish(new AssistantTextDeltaEvent(i.ToString()));
            Assert.InRange(mailbox.Count, 1, 4);
        }

        Assert.Equal(1, mailbox.Count);

        var read = await mailbox.ReadAsync();
        var delta = Assert.IsType<AssistantTextDeltaEvent>(read);
        var expected = string.Concat(Enumerable.Range(0, 100).Select(i => i.ToString()));
        Assert.Equal(expected, delta.Delta);
        Assert.Equal(0, mailbox.Count);
        Assert.False(mailbox.TryRead(out _));
    }

    [Fact]
    public async Task Tool_progress_for_same_tool_keeps_latest_value()
    {
        using var mailbox = new UiEventMailbox(capacity: 4);

        mailbox.Publish(new ToolProgressEvent("build", 100));
        mailbox.Publish(new ToolProgressEvent("build", 200));
        mailbox.Publish(new ToolProgressEvent("build", 300));

        Assert.Equal(1, mailbox.Count);

        var read = await mailbox.ReadAsync();
        var progress = Assert.IsType<ToolProgressEvent>(read);
        Assert.Equal("build", progress.ToolName);
        Assert.Equal(300, progress.ElapsedMs);
    }

    [Fact]
    public async Task Completion_and_error_events_are_not_coalesced()
    {
        using var mailbox = new UiEventMailbox(capacity: 4);

        mailbox.Publish(new AssistantTextCompletedEvent());
        mailbox.Publish(new AgentErrorEvent("boom"));

        Assert.Equal(2, mailbox.Count);

        var first = await mailbox.ReadAsync();
        var second = await mailbox.ReadAsync();
        Assert.IsType<AssistantTextCompletedEvent>(first);
        Assert.Equal(new AgentErrorEvent("boom"), second);
    }

    [Fact]
    public void Critical_event_evicts_oldest_coalescible_when_full()
    {
        using var mailbox = new UiEventMailbox(capacity: 2);

        mailbox.Publish(new ToolProgressEvent("a", 1));
        mailbox.Publish(new ToolProgressEvent("b", 2));
        Assert.Equal(2, mailbox.Count);

        mailbox.Publish(new AgentErrorEvent("boom"));
        Assert.Equal(2, mailbox.Count);

        var items = new List<UiEvent>();
        while (mailbox.TryRead(out var e))
        {
            items.Add(e!);
        }

        Assert.Equal(2, items.Count);
        Assert.Contains(new AgentErrorEvent("boom"), items);
        Assert.Contains(items, x => x is ToolProgressEvent tp && tp.ToolName == "b");
        Assert.DoesNotContain(items, x => x is ToolProgressEvent tp && tp.ToolName == "a");
    }

    [Fact]
    public void Count_never_exceeds_capacity_under_mixed_publishing()
    {
        using var mailbox = new UiEventMailbox(capacity: 2);

        for (var i = 0; i < 100; i++)
        {
            mailbox.Publish(new AssistantTextDeltaEvent("d"));
            mailbox.Publish(new ToolProgressEvent("a", i));
            mailbox.Publish(new ToolProgressEvent("b", i));
            Assert.True(mailbox.Count <= 2, $"count was {mailbox.Count}");
        }
    }

    [Fact]
    public async Task Blocked_critical_publisher_is_released_by_a_reader()
    {
        using var mailbox = new UiEventMailbox(capacity: 2);
        mailbox.Publish(new AgentErrorEvent("e1"));
        mailbox.Publish(new AgentErrorEvent("e2"));

        var publish = Task.Run(() => mailbox.Publish(new AgentErrorEvent("e3")));
        Assert.False(publish.Wait(200));
        Assert.Equal(2, mailbox.Count);

        var first = await mailbox.ReadAsync();
        Assert.Equal(new AgentErrorEvent("e1"), first);

        await publish.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, mailbox.Count);
    }

    [Fact]
    public async Task Blocked_critical_publisher_is_released_by_host_cancellation()
    {
        using var host = new CancellationTokenSource();
        using var mailbox = new UiEventMailbox(capacity: 2, host.Token);
        mailbox.Publish(new AgentErrorEvent("e1"));
        mailbox.Publish(new AgentErrorEvent("e2"));

        var publish = Task.Run(() => mailbox.Publish(new AgentErrorEvent("e3")));
        Assert.False(publish.Wait(200));

        host.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publish);
    }

    [Fact]
    public async Task Blocked_critical_publisher_is_released_by_dispose()
    {
        var mailbox = new UiEventMailbox(capacity: 2);
        mailbox.Publish(new AgentErrorEvent("e1"));
        mailbox.Publish(new AgentErrorEvent("e2"));

        var publish = Task.Run(() => mailbox.Publish(new AgentErrorEvent("e3")));
        Assert.False(publish.Wait(200));

        mailbox.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => publish);
    }

    [Fact]
    public async Task Dispose_wakes_a_blocked_reader()
    {
        var mailbox = new UiEventMailbox(capacity: 2);

        var read = mailbox.ReadAsync().AsTask();
        Assert.False(read.Wait(200));

        mailbox.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => read);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var mailbox = new UiEventMailbox(capacity: 2);
        mailbox.Dispose();
        mailbox.Dispose();
    }
}

public sealed class UiActorTests
{
    [Fact]
    public async Task Actor_drains_a_delta_burst_into_a_single_frame_and_block()
    {
        using var mailbox = new UiEventMailbox(capacity: 128);
        for (var i = 0; i < 100; i++)
        {
            mailbox.Publish(new AssistantTextDeltaEvent("x"));
        }

        var sink = new RecordingFrameSink();
        var actor = new UiActor(mailbox, sink, UiSessionSnapshot.Empty);
        using var cts = new CancellationTokenSource();
        var run = actor.RunAsync(cts.Token);

        await sink.FirstApplied.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await run;

        Assert.Equal(1, sink.ApplyCount);
        var block = Assert.IsType<AssistantTranscriptBlock>(Assert.Single(actor.Current.Transcript));
        Assert.Equal(new string('x', 100), block.Text);
    }

    [Fact]
    public async Task Actor_reports_every_event_to_the_observer_in_order()
    {
        using var mailbox = new UiEventMailbox(capacity: 128);
        mailbox.Publish(new UserPromptSubmittedEvent("a"));
        mailbox.Publish(new CommandOutputEvent("b"));
        mailbox.Publish(new WarningEvent("c"));

        var sink = new RecordingFrameSink();
        var observer = new RecordingObserver();
        var actor = new UiActor(mailbox, sink, UiSessionSnapshot.Empty, observer);
        using var cts = new CancellationTokenSource();
        var run = actor.RunAsync(cts.Token);

        await sink.FirstApplied.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await run;

        Assert.Equal(
            new UiEvent[]
            {
                new UserPromptSubmittedEvent("a"),
                new CommandOutputEvent("b"),
                new WarningEvent("c"),
            },
            observer.Events);
    }

    [Fact]
    public async Task Actor_propagates_frame_sink_exceptions()
    {
        using var mailbox = new UiEventMailbox(capacity: 8);
        mailbox.Publish(new AgentErrorEvent("boom"));

        var actor = new UiActor(mailbox, new ThrowingFrameSink(), UiSessionSnapshot.Empty);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.RunAsync(cts.Token));
    }

    [Fact]
    public async Task Actor_ends_cleanly_on_cancellation()
    {
        using var mailbox = new UiEventMailbox(capacity: 8);
        var actor = new UiActor(mailbox, NullUiFrameSink.Instance, UiSessionSnapshot.Empty);
        using var cts = new CancellationTokenSource();
        var run = actor.RunAsync(cts.Token);

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.IsCompletedSuccessfully);
    }

    private sealed class RecordingFrameSink : IUiFrameSink
    {
        private readonly TaskCompletionSource _firstApplied =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<UiSessionSnapshot> _snapshots = new();

        public Task FirstApplied => _firstApplied.Task;

        public int ApplyCount
        {
            get
            {
                lock (_snapshots)
                {
                    return _snapshots.Count;
                }
            }
        }

        public ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            lock (_snapshots)
            {
                _snapshots.Add(snapshot);
            }

            _firstApplied.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingObserver : IUiEventObserver
    {
        private readonly List<UiEvent> _events = new();

        public IReadOnlyList<UiEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToList();
                }
            }
        }

        public ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken)
        {
            lock (_events)
            {
                _events.Add(uiEvent);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingFrameSink : IUiFrameSink
    {
        public ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("render failed");
    }
}
