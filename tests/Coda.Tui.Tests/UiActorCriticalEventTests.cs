using Coda.Agent;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class UiActorCriticalEventTests
{
    [Fact]
    public async Task Activity_completion_survives_progress_eviction_and_is_observed_in_queue_order()
    {
        using var mailbox = new UiEventMailbox(capacity: 2);
        var observer = new RecordingObserver();
        var actor = new UiActor(mailbox, NullUiFrameSink.Instance, UiSessionSnapshot.Empty, observer);
        var first = new ToolCallIdentity("turn", "activity", "first", "source");
        var second = new ToolCallIdentity("turn", "activity", "second", "source");
        var summary = new ToolActivitySummary("turn", "activity", 2, 0, 0, 0, "build");

        mailbox.Publish(new ToolProgressEvent("build", 100, first));
        mailbox.Publish(new ToolProgressEvent("build", 200, second));
        mailbox.Publish(new ToolActivityCompletedEvent(summary));

        using var cancellation = new CancellationTokenSource();
        var run = actor.RunAsync(cancellation.Token);
        await actor.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await run;

        Assert.Equal(
            new UiEvent[]
            {
                new ToolProgressEvent("build", 200, second),
                new ToolActivityCompletedEvent(summary),
            },
            observer.Events);
    }

    private sealed class RecordingObserver : IUiEventObserver
    {
        private readonly List<UiEvent> events = new();

        public IReadOnlyList<UiEvent> Events => this.events;

        public ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken)
        {
            this.events.Add(uiEvent);
            return ValueTask.CompletedTask;
        }
    }
}
