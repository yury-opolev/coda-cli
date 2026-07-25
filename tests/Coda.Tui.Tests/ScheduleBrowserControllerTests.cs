using Coda.Agent.Scheduling;
using Coda.Tui.Ui.Schedule;
using System.Collections.Immutable;
using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Tests;

/// <summary>
/// Headless coverage for <see cref="ScheduleBrowserController"/>: list seeding, live-refresh signal,
/// delete confirm flow, create form flow (success and failure), and state transitions.
/// Mirrors the controller-test pattern from <see cref="TaskBrowserControllerTests"/>.
/// </summary>
public sealed class ScheduleBrowserControllerTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);

    private static ScheduledTaskReadModel MakeModel(string id, string? name = null) =>
        ScheduleCommandTests.ReadModel(id, name ?? id, "every 1h", "UTC", NowUtc);

    // ── Open / Close ──────────────────────────────────────────────────────────

    [Fact]
    public void Open_seeds_state_from_initial_list()
    {
        var control = new FakeScheduleControl(
            [MakeModel("s1"), MakeModel("s2")]);
        using var controller = new ScheduleBrowserController(() => control, PlainUiPromptService.Instance);

        controller.Open();

        Assert.Equal(2, controller.State.Rows.Count);
        Assert.NotNull(controller.State.SelectedId);
        Assert.Equal("s1", controller.State.SelectedId);
    }

    [Fact]
    public void Open_with_null_provider_leaves_empty_state()
    {
        using var controller = new ScheduleBrowserController(() => null, PlainUiPromptService.Instance);

        controller.Open();

        Assert.Empty(controller.State.Rows);
        Assert.Null(controller.State.SelectedId);
    }

    [Fact]
    public void Close_clears_state_and_raises_changed()
    {
        var control = new FakeScheduleControl([MakeModel("s1")]);
        using var controller = new ScheduleBrowserController(() => control, PlainUiPromptService.Instance);
        controller.Open();

        var changed = false;
        controller.Changed += () => changed = true;
        controller.Close();

        Assert.Empty(controller.State.Rows);
        Assert.True(changed);
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    [Fact]
    public void MoveSelection_down_advances_selected_index()
    {
        var control = new FakeScheduleControl(
            [MakeModel("s1"), MakeModel("s2"), MakeModel("s3")]);
        using var controller = new ScheduleBrowserController(() => control, PlainUiPromptService.Instance);
        controller.Open();

        controller.MoveSelection(1);

        Assert.Equal("s2", controller.State.SelectedId);
    }

    [Fact]
    public void MoveSelection_up_goes_back_to_first()
    {
        var control = new FakeScheduleControl(
            [MakeModel("s1"), MakeModel("s2")]);
        using var controller = new ScheduleBrowserController(() => control, PlainUiPromptService.Instance);
        controller.Open();
        controller.MoveSelection(1); // -> s2

        controller.MoveSelection(-1); // -> s1

        Assert.Equal("s1", controller.State.SelectedId);
    }

    [Fact]
    public void MoveSelection_clamps_at_boundaries()
    {
        var control = new FakeScheduleControl(
            [MakeModel("s1"), MakeModel("s2")]);
        using var controller = new ScheduleBrowserController(() => control, PlainUiPromptService.Instance);
        controller.Open();

        controller.MoveSelection(-10); // clamp to 0
        Assert.Equal("s1", controller.State.SelectedId);

        controller.MoveSelection(100); // clamp to last
        Assert.Equal("s2", controller.State.SelectedId);
    }

    // ── NotifyScheduleChanged / PumpAsync ─────────────────────────────────────

    [Fact]
    public async Task NotifyScheduleChanged_triggers_list_refresh_via_pump()
    {
        var control = new FakeScheduleControl([MakeModel("s1")]);
        using var controller = new ScheduleBrowserController(() => control, PlainUiPromptService.Instance);
        controller.Open();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pumpTask = controller.PumpAsync(cts.Token);

        // Add a new item and signal the controller.
        control.Items.Add(MakeModel("s2"));
        var changed = new TaskCompletionSource();
        controller.Changed += () => changed.TrySetResult();

        controller.NotifyScheduleChanged();
        await changed.Task;

        Assert.Equal(2, controller.State.Rows.Count);
        cts.Cancel();
        await pumpTask;
    }

    [Fact]
    public async Task PumpAsync_exits_on_cancellation()
    {
        var control = new FakeScheduleControl([]);
        using var controller = new ScheduleBrowserController(() => control, PlainUiPromptService.Instance);
        controller.Open();

        using var cts = new CancellationTokenSource();
        var pumpTask = controller.PumpAsync(cts.Token);
        cts.Cancel();
        await pumpTask; // must not throw OperationCanceledException
    }

    // ── Changed subscriber count ───────────────────────────────────────────────

    [Fact]
    public void ChangedSubscriberCount_starts_at_zero()
    {
        using var controller = new ScheduleBrowserController(
            () => new FakeScheduleControl([]), PlainUiPromptService.Instance);

        Assert.Equal(0, controller.ChangedSubscriberCount);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_confirmed_removes_item_and_refreshes()
    {
        var control = new FakeScheduleControl([MakeModel("s1"), MakeModel("s2")]);
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ImmutableArray.Create("yes"), null)); // confirmed
        using var controller = new ScheduleBrowserController(() => control, prompts);
        controller.Open();

        await controller.DeleteSelectedAsync(CancellationToken.None);

        Assert.Single(control.Items);
        Assert.Equal("s2", control.Items[0].Id);
    }

    [Fact]
    public async Task Delete_cancelled_leaves_item_intact()
    {
        var control = new FakeScheduleControl([MakeModel("s1")]);
        var prompts = new RecordingPromptService(
            new UiPromptResponse(true, [], null)); // cancelled
        using var controller = new ScheduleBrowserController(() => control, prompts);
        controller.Open();

        await controller.DeleteSelectedAsync(CancellationToken.None);

        Assert.Single(control.Items);
    }

    [Fact]
    public async Task Delete_denied_leaves_item_intact()
    {
        var control = new FakeScheduleControl([MakeModel("s1")]);
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ImmutableArray.Create("no"), null)); // denied
        using var controller = new ScheduleBrowserController(() => control, prompts);
        controller.Open();

        await controller.DeleteSelectedAsync(CancellationToken.None);

        Assert.Single(control.Items);
    }

    [Fact]
    public async Task Delete_with_no_selection_does_nothing()
    {
        var control = new FakeScheduleControl([]);
        using var controller = new ScheduleBrowserController(() => control, PlainUiPromptService.Instance);
        controller.Open();

        // No exception; nothing to delete.
        await controller.DeleteSelectedAsync(CancellationToken.None);
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_interval_builds_correct_request_and_calls_service()
    {
        var control = new FakeScheduleControl([]);
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ImmutableArray.Create("interval"), null), // kind: interval
            new UiPromptResponse(false, [], "2h"),                                 // value: 2h
            new UiPromptResponse(false, [], null),                                 // timezone: empty
            new UiPromptResponse(false, [], "Do the thing"),                       // prompt
            new UiPromptResponse(false, [], "My schedule"));                       // name
        using var controller = new ScheduleBrowserController(() => control, prompts);
        controller.Open();

        await controller.CreateAsync(CancellationToken.None);

        var req = Assert.Single(control.CreateRequests);
        Assert.Equal("2h", req.Every);
        Assert.Equal("Do the thing", req.Prompt);
        Assert.Equal("My schedule", req.Name);
        Assert.Null(req.TimeZoneId);
    }

    [Fact]
    public async Task Create_at_kind_sets_at_field()
    {
        var control = new FakeScheduleControl([]);
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ImmutableArray.Create("at"), null),
            new UiPromptResponse(false, [], "2026-07-25T15:00:00Z"),
            new UiPromptResponse(false, [], null),
            new UiPromptResponse(false, [], "Prompt text"),
            new UiPromptResponse(false, [], null));
        using var controller = new ScheduleBrowserController(() => control, prompts);
        controller.Open();

        await controller.CreateAsync(CancellationToken.None);

        var req = Assert.Single(control.CreateRequests);
        Assert.Equal("2026-07-25T15:00:00Z", req.At);
        Assert.Null(req.Every);
    }

    [Fact]
    public async Task Create_cron_kind_sets_cron_field()
    {
        var control = new FakeScheduleControl([]);
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ImmutableArray.Create("cron"), null),
            new UiPromptResponse(false, [], "0 9 * * 1"),
            new UiPromptResponse(false, [], "Europe/Berlin"),
            new UiPromptResponse(false, [], "Standup"),
            new UiPromptResponse(false, [], null));
        using var controller = new ScheduleBrowserController(() => control, prompts);
        controller.Open();

        await controller.CreateAsync(CancellationToken.None);

        var req = Assert.Single(control.CreateRequests);
        Assert.Equal("0 9 * * 1", req.Cron);
        Assert.Equal("Europe/Berlin", req.TimeZoneId);
    }

    [Fact]
    public async Task Create_fail_sets_status_message_with_parser_error()
    {
        var control = new FakeScheduleControl([]);
        control.CreateError = "Invalid cron expression: bad field";
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ImmutableArray.Create("cron"), null),
            new UiPromptResponse(false, [], "bad cron"),
            new UiPromptResponse(false, [], null),
            new UiPromptResponse(false, [], "Prompt"),
            new UiPromptResponse(false, [], null));
        using var controller = new ScheduleBrowserController(() => control, prompts);
        controller.Open();

        await controller.CreateAsync(CancellationToken.None);

        Assert.Contains("Invalid cron expression", controller.State.StatusMessage);
    }

    [Fact]
    public async Task Create_ok_refreshes_list()
    {
        var newModel = MakeModel("s-new", "new sched");
        var control = new FakeScheduleControl([]);
        control.CreateModel = newModel;
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ImmutableArray.Create("interval"), null),
            new UiPromptResponse(false, [], "1h"),
            new UiPromptResponse(false, [], null),
            new UiPromptResponse(false, [], "Do it"),
            new UiPromptResponse(false, [], null));
        using var controller = new ScheduleBrowserController(() => control, prompts);
        controller.Open();

        await controller.CreateAsync(CancellationToken.None);

        Assert.Contains(controller.State.Rows, r => r.Id == "s-new");
    }

    [Fact]
    public async Task Create_cancelled_at_kind_step_does_nothing()
    {
        var control = new FakeScheduleControl([]);
        var prompts = new RecordingPromptService(
            new UiPromptResponse(true, [], null)); // cancelled at kind step
        using var controller = new ScheduleBrowserController(() => control, prompts);
        controller.Open();

        await controller.CreateAsync(CancellationToken.None);

        Assert.Empty(control.CreateRequests);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fake <see cref="IScheduleControl"/> that records create requests and returns a configurable result.
    /// </summary>
    private sealed class FakeScheduleControl(IEnumerable<ScheduledTaskReadModel> initial)
        : IScheduleControl
    {
        public List<ScheduledTaskReadModel> Items { get; } = [.. initial];
        public List<ScheduleCreateRequest> CreateRequests { get; } = [];
        public string? CreateError { get; set; }
        public ScheduledTaskReadModel? CreateModel { get; set; }

        public IReadOnlyList<ScheduledTaskReadModel> List() => Items;

        public ScheduleCreateResult Create(ScheduleCreateRequest request)
        {
            CreateRequests.Add(request);
            if (CreateError is not null)
            {
                return ScheduleCreateResult.Fail(CreateError);
            }

            if (CreateModel is not null)
            {
                Items.Add(CreateModel);
                return ScheduleCreateResult.Ok(CreateModel);
            }

            return ScheduleCreateResult.Fail("no model configured");
        }

        public bool Delete(string id) => Items.RemoveAll(m => m.Id == id) > 0;
    }
}
