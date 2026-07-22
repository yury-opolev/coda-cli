using Coda.Tui.Agent;
using Coda.Tui.Repl;
using Coda.Tui.Ui;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class PendingSteeringIntegrationTests
{
    [Fact]
    public void Reducer_delivers_only_the_matching_pending_block_in_place()
    {
        var at = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var firstBlockId = Guid.NewGuid();
        var state = UiReducer.Reduce(
            UiSessionSnapshot.Empty,
            new UserPromptEnqueuedEvent(firstBlockId, "first", "entry-1", at));
        state = UiReducer.Reduce(
            state,
            new UserPromptEnqueuedEvent(Guid.NewGuid(), "second", "entry-2", at.AddMinutes(1)));

        state = UiReducer.Reduce(state, new SteeringDeliveredEvent(["entry-1"]));

        var delivered = Assert.IsType<UserTranscriptBlock>(state.Transcript[0]);
        Assert.Equal(firstBlockId, delivered.Id);
        Assert.Equal("first", delivered.Text);
        Assert.Equal(at, delivered.SentAt);
        Assert.IsType<PendingUserTranscriptBlock>(state.Transcript[1]);
    }

    [Fact]
    public void Reducer_recall_removes_only_still_pending_matching_entries()
    {
        var state = UiReducer.Reduce(
            UiSessionSnapshot.Empty,
            new UserPromptEnqueuedEvent(Guid.NewGuid(), "first", "entry-1", DateTimeOffset.UtcNow));
        state = UiReducer.Reduce(
            state,
            new UserPromptEnqueuedEvent(Guid.NewGuid(), "second", "entry-2", DateTimeOffset.UtcNow));
        state = UiReducer.Reduce(state, new SteeringDeliveredEvent(["entry-1"]));

        state = UiReducer.Reduce(state, new PendingSteeringRecalledEvent(["entry-1", "entry-2"]));

        var delivered = Assert.IsType<UserTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("first", delivered.Text);
    }

    [Fact]
    public void Pending_user_format_has_user_style_and_visible_annotation()
    {
        var lines = TranscriptBlockFormatter.Format(
            new PendingUserTranscriptBlock(Guid.NewGuid(), "queued", "entry", DateTimeOffset.UtcNow),
            80);

        var line = Assert.Single(lines);
        Assert.Equal(TranscriptRole.User, line.Role);
        Assert.True(line.FillWidth);
        Assert.Contains("queued", line.Text);
        Assert.Contains("pending", line.RightText ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sink_forwards_steering_delivery_ids()
    {
        var events = new List<UiEvent>();
        var sink = new TuiAgentSink(new CollectingPublisher(events));

        sink.OnSteeringDelivered(["a", "b"]);

        var delivered = Assert.IsType<SteeringDeliveredEvent>(Assert.Single(events));
        Assert.Equal(["a", "b"], delivered.QueueEntryIds);
    }

    [Fact]
    public async Task Busy_prompt_is_enqueued_and_published()
    {
        var events = new List<UiEvent>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (_, _) => { started.TrySetResult(); await release.Task; },
            tryInterrupt: () => false,
            publisher: new CollectingPublisher(events),
            initialSnapshot: UiSessionSnapshot.Empty,
            steer: text => text == "queued" ? "entry-1" : null,
            recallSteering: () => []);

        controller.OnSubmitted("run");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        controller.OnSubmitted("queued");

        var queued = Assert.IsType<UserPromptEnqueuedEvent>(Assert.Single(events));
        Assert.Equal("queued", queued.Text);
        Assert.Equal("entry-1", queued.QueueEntryId);

        release.SetResult();
        await controller.WaitForDispatchAsync();
    }

    [Fact]
    public async Task Rejected_busy_prompt_dispatches_once_after_turn_releases()
    {
        var dispatched = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (text, _) =>
            {
                dispatched.Add(text);
                if (text == "run")
                {
                    started.TrySetResult();
                    await release.Task;
                }
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty,
            steer: _ => null,
            recallSteering: () => []);

        controller.OnSubmitted("run");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        controller.OnSubmitted("next");
        release.SetResult();

        await controller.WaitForDispatchAsync();
        Assert.Equal(["run", "next"], dispatched);
    }

    [Fact]
    public void Recall_restores_all_pending_messages_as_a_single_draft()
    {
        var events = new List<UiEvent>();
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: new CollectingPublisher(events),
            initialSnapshot: UiSessionSnapshot.Empty,
            steer: _ => null,
            recallSteering: () =>
            [
                new Coda.Agent.SteeringEntry("one", "first", DateTimeOffset.UtcNow),
                new Coda.Agent.SteeringEntry("two", "second", DateTimeOffset.UtcNow),
            ]);

        var draft = controller.RecallSteering();

        Assert.Equal("first\n\nsecond", draft);
        var recalled = Assert.IsType<PendingSteeringRecalledEvent>(Assert.Single(events));
        Assert.Equal(["one", "two"], recalled.QueueEntryIds);
    }

    private sealed class CollectingPublisher(List<UiEvent> events) : IUiEventPublisher
    {
        public void Publish(UiEvent uiEvent) => events.Add(uiEvent);
    }
}
