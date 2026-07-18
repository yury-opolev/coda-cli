using System.Collections.Immutable;
using System.Text.Json;
using Coda.Agent;
using Coda.Tui.Agent;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.State;
using LlmClient;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class UiPromptServiceTests
{
    [Fact]
    public async Task Actor_prompt_round_trips_through_request_and_response_events()
    {
        using var mailbox = new UiEventMailbox(8);
        var service = new ActorUiPromptService(mailbox);
        var requestTask = service.RequestAsync(UiPromptRequest.Confirm("Delete?", defaultValue: false));
        var requested = Assert.IsType<UiPromptRequestedEvent>(await mailbox.ReadAsync());

        service.Complete(new UiPromptResponseSubmittedEvent(
            requested.Request.Id,
            new UiPromptResponse(false, ["yes"], null)));

        var response = await requestTask;
        Assert.Equal(new[] { "yes" }, response.SelectedIds.ToArray());
    }

    [Fact]
    public async Task Plain_prompt_denies_confirmation_and_cancels_selection()
    {
        var service = PlainUiPromptService.Instance;

        var confirm = await service.RequestAsync(UiPromptRequest.Confirm("Allow?", defaultValue: false));
        var select = await service.RequestAsync(UiPromptRequest.Select("Choose", [new("a", "A")]));

        Assert.False(service.IsInteractive);
        Assert.Equal(new[] { "no" }, confirm.SelectedIds.ToArray());
        Assert.True(select.Cancelled);
    }

    [Fact]
    public async Task Spectre_prompt_remains_available_for_fallback()
    {
        using var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushKey(ConsoleKey.Enter);
        var service = new SpectreUiPromptService(console);

        var response = await service.RequestAsync(
            UiPromptRequest.Select("Choose", [new("a", "A"), new("b", "B")]));

        Assert.Equal(new[] { "a" }, response.SelectedIds.ToArray());
    }

    [Fact]
    public async Task Actor_cancellation_cancels_only_the_cancelled_prompt()
    {
        using var mailbox = new UiEventMailbox(8);
        var service = new ActorUiPromptService(mailbox);
        using var cts = new CancellationTokenSource();

        var cancelledTask = service.RequestAsync(UiPromptRequest.Confirm("A?", defaultValue: false), cts.Token);
        var survivingTask = service.RequestAsync(UiPromptRequest.Confirm("B?", defaultValue: false));

        Assert.IsType<UiPromptRequestedEvent>(await mailbox.ReadAsync());
        var surviving = Assert.IsType<UiPromptRequestedEvent>(await mailbox.ReadAsync());

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledTask);
        Assert.False(survivingTask.IsCompleted);

        Assert.True(service.Complete(new UiPromptResponseSubmittedEvent(
            surviving.Request.Id,
            new UiPromptResponse(false, ["yes"], null))));
        var response = await survivingTask;
        Assert.Equal(new[] { "yes" }, response.SelectedIds.ToArray());
    }

    [Fact]
    public async Task Actor_pre_cancelled_request_throws_before_publishing_any_event()
    {
        using var mailbox = new UiEventMailbox(8);
        var service = new ActorUiPromptService(mailbox);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RequestAsync(UiPromptRequest.Confirm("A?", defaultValue: false), cts.Token));

        Assert.Equal(0, mailbox.Count);
    }

    [Fact]
    public async Task Actor_cancellation_publishes_response_that_clears_pending_prompt()
    {
        using var mailbox = new UiEventMailbox(8);
        var service = new ActorUiPromptService(mailbox);
        using var cts = new CancellationTokenSource();

        var task = service.RequestAsync(UiPromptRequest.Confirm("A?", defaultValue: false), cts.Token);
        var requested = Assert.IsType<UiPromptRequestedEvent>(await mailbox.ReadAsync());

        // PendingPrompt is set once the request event is reduced.
        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, requested);
        Assert.Same(requested.Request, state.PendingPrompt);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        // A matching cancellation response is published so the reducer can clear the stale prompt.
        Assert.Equal(1, mailbox.Count);
        var response = Assert.IsType<UiPromptResponseSubmittedEvent>(await mailbox.ReadAsync());
        Assert.Equal(requested.Request.Id, response.RequestId);
        Assert.True(response.Response.Cancelled);
        Assert.Empty(response.Response.SelectedIds);
        Assert.Null(response.Response.Text);

        var cleared = UiReducer.Reduce(state, response);
        Assert.Null(cleared.PendingPrompt);
    }

    [Fact]
    public async Task Actor_cancellation_racing_publish_orders_request_before_cancellation_response()
    {
        using var cts = new CancellationTokenSource();

        // The publisher cancels the token the instant it receives the request event, exposing any
        // interleaving between publishing the request and registering the cancellation callback.
        var publisher = new CancelOnRequestPublisher(cts);
        var service = new ActorUiPromptService(publisher);

        var task = service.RequestAsync(UiPromptRequest.Confirm("A?", defaultValue: false), cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        // The request must be recorded (and thus reduced) before its cancellation response;
        // otherwise the reducer is left with a stale PendingPrompt.
        Assert.Equal(2, publisher.Events.Count);
        var requested = Assert.IsType<UiPromptRequestedEvent>(publisher.Events[0]);
        var response = Assert.IsType<UiPromptResponseSubmittedEvent>(publisher.Events[1]);
        Assert.Equal(requested.Request.Id, response.RequestId);
        Assert.True(response.Response.Cancelled);

        var state = UiSessionSnapshot.Empty;
        foreach (var uiEvent in publisher.Events)
        {
            state = UiReducer.Reduce(state, uiEvent);
        }

        Assert.Null(state.PendingPrompt);
    }

    [Fact]
    public void Spectre_fallback_factory_is_interactive_for_interactive_console()
    {
        using var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;

        var service = UiPromptServiceFactory.ForSpectreFallback(console);

        Assert.IsType<SpectreUiPromptService>(service);
        Assert.True(service.IsInteractive);
    }

    [Fact]
    public async Task Actor_unknown_or_duplicate_response_does_not_complete_other_prompts()
    {
        using var mailbox = new UiEventMailbox(8);
        var service = new ActorUiPromptService(mailbox);
        var task = service.RequestAsync(UiPromptRequest.Confirm("A?", defaultValue: false));
        var requested = Assert.IsType<UiPromptRequestedEvent>(await mailbox.ReadAsync());

        Assert.False(service.Complete(new UiPromptResponseSubmittedEvent(
            Guid.NewGuid(),
            new UiPromptResponse(false, ["yes"], null))));
        Assert.False(task.IsCompleted);

        Assert.True(service.Complete(new UiPromptResponseSubmittedEvent(
            requested.Request.Id,
            new UiPromptResponse(false, ["no"], null))));
        var response = await task;
        Assert.Equal(new[] { "no" }, response.SelectedIds.ToArray());

        Assert.False(service.Complete(new UiPromptResponseSubmittedEvent(
            requested.Request.Id,
            new UiPromptResponse(false, ["yes"], null))));
    }

    [Fact]
    public void Reducer_sets_and_clears_pending_prompt_only_for_matching_id()
    {
        var request = UiPromptRequest.Confirm("A?", defaultValue: false);
        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, new UiPromptRequestedEvent(request));
        Assert.Same(request, state.PendingPrompt);

        var untouched = UiReducer.Reduce(
            state,
            new UiPromptResponseSubmittedEvent(Guid.NewGuid(), new UiPromptResponse(false, ["yes"], null)));
        Assert.Same(request, untouched.PendingPrompt);

        var cleared = UiReducer.Reduce(
            state,
            new UiPromptResponseSubmittedEvent(request.Id, new UiPromptResponse(false, ["yes"], null)));
        Assert.Null(cleared.PendingPrompt);
    }

    [Fact]
    public async Task Permission_adapter_publishes_events_and_maps_response_to_bool()
    {
        var allowPrompts = new RecordingPromptService(new UiPromptResponse(false, ["yes"], null));
        var allowEvents = new RecordingEventPublisher();
        var allowed = await new TuiPermissionPrompt(allowPrompts, allowEvents)
            .RequestAsync(new FakeTool("write_file"), "path=x");

        Assert.True(allowed);
        var requested = Assert.IsType<PermissionRequestedEvent>(allowEvents.Events[0]);
        Assert.Equal("write_file", requested.ToolName);
        var resolved = Assert.IsType<PermissionResolvedEvent>(allowEvents.Events[1]);
        Assert.True(resolved.Allowed);

        var denyPrompts = new RecordingPromptService(new UiPromptResponse(false, ["no"], null));
        var denyEvents = new RecordingEventPublisher();
        var denied = await new TuiPermissionPrompt(denyPrompts, denyEvents)
            .RequestAsync(new FakeTool("write_file"), "path=x");
        Assert.False(denied);
        Assert.False(Assert.IsType<PermissionResolvedEvent>(denyEvents.Events[1]).Allowed);
    }

    [Fact]
    public async Task Permission_adapter_denies_when_not_interactive()
    {
        var events = new RecordingEventPublisher();
        var adapter = new TuiPermissionPrompt(PlainUiPromptService.Instance, events);

        var allowed = await adapter.RequestAsync(new FakeTool("write_file"), "path=x");

        Assert.False(allowed);
        Assert.IsType<PermissionRequestedEvent>(events.Events[0]);
        Assert.False(Assert.IsType<PermissionResolvedEvent>(events.Events[1]).Allowed);
    }

    [Fact]
    public async Task Plan_adapter_sets_accept_edits_only_on_approval()
    {
        var approveSession = new SessionState("claude-ai")
        {
            PermissionMode = PermissionMode.Plan,
        };
        var approveEvents = new RecordingEventPublisher();
        var approver = new TuiPlanApprover(
            new RecordingPromptService(new UiPromptResponse(false, ["yes"], null)),
            approveEvents,
            approveSession);

        var approved = await approver.ApproveAsync("do the thing");

        Assert.True(approved);
        Assert.Equal(PermissionMode.AcceptEdits, approveSession.PermissionMode);
        Assert.IsType<PlanApprovalRequestedEvent>(approveEvents.Events[0]);
        Assert.True(Assert.IsType<PlanApprovalResolvedEvent>(approveEvents.Events[1]).Approved);

        var rejectSession = new SessionState("claude-ai");
        var rejectEvents = new RecordingEventPublisher();
        var rejected = await new TuiPlanApprover(
            new RecordingPromptService(new UiPromptResponse(false, ["no"], null)),
            rejectEvents,
            rejectSession).ApproveAsync("do the thing");

        Assert.False(rejected);
        Assert.Equal(PermissionMode.Default, rejectSession.PermissionMode);
        Assert.False(Assert.IsType<PlanApprovalResolvedEvent>(rejectEvents.Events[1]).Approved);
    }

    [Fact]
    public async Task User_question_adapter_preserves_option_order_and_joins_labels()
    {
        var options = new[] { "Red", "Green", "Blue" };
        var events = new RecordingEventPublisher();

        // Selected ids arrive out of order; the answer must follow original option order.
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["2", "0"], null));
        var adapter = new TuiUserQuestionPrompt(prompts, events);

        var answer = await adapter.AskAsync("Pick colors", options, multiSelect: true);

        Assert.Equal("Red, Blue", answer);
        var requested = Assert.IsType<UserQuestionRequestedEvent>(events.Events[0]);
        Assert.True(requested.MultiSelect);
        Assert.Equal(UiPromptKind.SelectMany, Assert.Single(prompts.Requests).Kind);
        Assert.Equal(new[] { "Red", "Green", "Blue" }, prompts.Requests[0].Options.Select(o => o.Label));
        Assert.Equal("Red, Blue", Assert.IsType<UserQuestionResolvedEvent>(events.Events[1]).Answer);
    }

    [Fact]
    public async Task User_question_adapter_maps_single_select_answer()
    {
        var options = new[] { "One", "Two" };
        var events = new RecordingEventPublisher();
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["1"], "Two"));
        var adapter = new TuiUserQuestionPrompt(prompts, events);

        var answer = await adapter.AskAsync("Pick", options, multiSelect: false);

        Assert.Equal("Two", answer);
        Assert.Equal(UiPromptKind.SelectOne, prompts.Requests[0].Kind);
    }

    private sealed class RecordingEventPublisher : IUiEventPublisher
    {
        public List<UiEvent> Events { get; } = [];

        public void Publish(UiEvent uiEvent) => this.Events.Add(uiEvent);
    }

    private sealed class CancelOnRequestPublisher : IUiEventPublisher
    {
        private readonly CancellationTokenSource _cts;

        public CancelOnRequestPublisher(CancellationTokenSource cts) => this._cts = cts;

        public List<UiEvent> Events { get; } = [];

        public void Publish(UiEvent uiEvent)
        {
            // Cancel before recording the request. If the cancellation callback is registered
            // before the request is published (the bug), Cancel() runs it synchronously here and
            // records the cancellation response ahead of this request, inverting the order.
            if (uiEvent is UiPromptRequestedEvent)
            {
                this._cts.Cancel();
            }

            this.Events.Add(uiEvent);
        }
    }

    private sealed class FakeTool : ITool
    {
        public FakeTool(string name) => this.Name = name;

        public string Name { get; }

        public string Description => "fake";

        public string InputSchemaJson => "{}";

        public bool IsReadOnly => false;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult(string.Empty));
    }
}
