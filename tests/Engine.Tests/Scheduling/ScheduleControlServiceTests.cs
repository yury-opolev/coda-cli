using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Scheduling;
using Coda.Agent.Tools;

namespace Engine.Tests.Scheduling;

/// <summary>
/// Unit tests for <see cref="ScheduleControlService"/>: list/create/delete operations,
/// validation passthrough, no-store path, and time/zone seam injection.
/// </summary>
public sealed class ScheduleControlServiceTests
{
    private static readonly DateTimeOffset Epoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeRuntimeView(ScheduleRuntimeState state, string? matchId = null) : IScheduleRuntimeView
    {
        public bool TryGetState(string scheduleId, out ScheduleRuntimeState s)
        {
            if (matchId is null || scheduleId == matchId)
            {
                s = state;
                return true;
            }

            s = null!;
            return false;
        }

        public IReadOnlyList<ScheduleRuntimeSnapshot> GetSnapshot() =>
            [new ScheduleRuntimeSnapshot(matchId ?? string.Empty, state.Status, state.ActiveTaskId)];
    }

    private static ScheduledTaskStore SeedInterval(out string id)
    {
        var store = new ScheduledTaskStore();
        var draft = new ScheduleDefinitionDraft(
            Name: null,
            Kind: ScheduleKind.Interval,
            Prompt: "hello world prompt",
            Interval: TimeSpan.FromMinutes(5),
            AtUtc: null,
            Cron: null,
            TimeZoneId: "UTC",
            NextRunUtc: Epoch.AddMinutes(5));
        var task = store.Add(draft, Epoch);
        id = task.Id;
        return store;
    }

    // ── List ─────────────────────────────────────────────────────────────────

    [Fact]
    public void List_null_store_returns_empty()
    {
        var svc = new ScheduleControlService(null, runtimeView: null);
        Assert.Empty(svc.List());
    }

    [Fact]
    public void List_empty_store_returns_empty()
    {
        var svc = new ScheduleControlService(new ScheduledTaskStore(), runtimeView: null);
        Assert.Empty(svc.List());
    }

    [Fact]
    public void List_returns_one_read_model_per_definition()
    {
        var store = SeedInterval(out var id);
        var svc = new ScheduleControlService(store, runtimeView: null);

        var items = svc.List();

        var item = Assert.Single(items);
        Assert.Equal(id, item.Id);
        Assert.Equal(ScheduleKind.Interval, item.Kind);
        Assert.Equal("hello world prompt", item.Prompt);
        Assert.Equal("UTC", item.TimeZone);
        Assert.Equal(Epoch.AddMinutes(5), item.NextRunUtc);
        Assert.NotEmpty(item.Rule);
        Assert.Contains("interval", item.Rule, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5m", item.Rule);
        Assert.Equal(ScheduleRuntimeStatus.Idle, item.State);
        Assert.Null(item.ActiveTaskId);
        Assert.Null(item.LastOutcome);
    }

    [Fact]
    public void List_includes_runtime_state_running()
    {
        var store = SeedInterval(out var id);
        var view = new FakeRuntimeView(new ScheduleRuntimeState(ScheduleRuntimeStatus.Running, "task-99"), id);
        var svc = new ScheduleControlService(store, view);

        var item = Assert.Single(svc.List());

        Assert.Equal(ScheduleRuntimeStatus.Running, item.State);
        Assert.Equal("task-99", item.ActiveTaskId);
    }

    [Fact]
    public void List_includes_runtime_state_pending()
    {
        var store = SeedInterval(out var id);
        var view = new FakeRuntimeView(new ScheduleRuntimeState(ScheduleRuntimeStatus.Pending, null), id);
        var svc = new ScheduleControlService(store, view);

        var item = Assert.Single(svc.List());

        Assert.Equal(ScheduleRuntimeStatus.Pending, item.State);
        Assert.Null(item.ActiveTaskId);
    }

    [Fact]
    public void List_shows_last_terminal_outcome()
    {
        var store = new ScheduledTaskStore();
        var draft = new ScheduleDefinitionDraft(
            "nightly", ScheduleKind.Cron, "backup", null, null, "0 0 * * *", "UTC",
            new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var task = store.Add(draft, Epoch);
        var terminal = new ScheduleTerminalMetadata(
            ScheduleTerminalOutcome.Failed,
            new DateTimeOffset(2025, 1, 1, 0, 5, 0, TimeSpan.Zero),
            "boom");
        store.Replace(task with { LastTerminalOutcome = terminal });

        var svc = new ScheduleControlService(store, runtimeView: null);
        var item = Assert.Single(svc.List());

        Assert.NotNull(item.LastOutcome);
        Assert.Equal(ScheduleTerminalOutcome.Failed, item.LastOutcome!.Outcome);
        Assert.Equal("boom", item.LastOutcome.Summary);
    }

    [Fact]
    public void List_invalid_timezone_falls_back_gracefully()
    {
        var store = new ScheduledTaskStore();
        var draft = new ScheduleDefinitionDraft(
            null, ScheduleKind.Cron, "x", null, null, "0 0 * * *", "Not/A_Zone",
            new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero));
        store.Add(draft, Epoch);

        var svc = new ScheduleControlService(store, runtimeView: null);
        // Should not throw; the local label falls back to UTC.
        var item = Assert.Single(svc.List());
        Assert.Contains("UTC", item.NextRunLocalLabel);
    }

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public void Create_interval_stores_and_returns_read_model()
    {
        var store = new ScheduledTaskStore();
        var svc = new ScheduleControlService(store, runtimeView: null, new FixedTimeProvider(Epoch));

        var result = svc.Create(new ScheduleCreateRequest(null, "ping", Every: "5m", null, null, null));

        Assert.True(result.IsSuccess);
        var model = result.Task!;
        var stored = Assert.Single(store.Items);
        Assert.Equal(stored.Id, model.Id);
        Assert.Equal(ScheduleKind.Interval, model.Kind);
        Assert.Equal("ping", model.Prompt);
        Assert.Equal(TimeSpan.FromMinutes(5), stored.Interval);
        Assert.Equal(Epoch.AddMinutes(5), model.NextRunUtc);
        Assert.Equal(ScheduleRuntimeStatus.Idle, model.State);
        Assert.Null(model.ActiveTaskId);
        Assert.Null(model.LastOutcome);
        Assert.Contains("interval", model.Rule, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_at_local_uses_injected_zone_for_local_display()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test/Plus2", TimeSpan.FromHours(2), "Test/Plus2", "Test/Plus2");
        var store = new ScheduledTaskStore();
        var svc = new ScheduleControlService(
            store, runtimeView: null, new FixedTimeProvider(Epoch), () => zone);

        var result = svc.Create(
            new ScheduleCreateRequest(null, "wake", null, At: "2025-06-01T09:00:00", null, null));

        Assert.True(result.IsSuccess);
        var model = result.Task!;
        // Local 09:00 in +2 → UTC 07:00
        Assert.Equal(new DateTimeOffset(2025, 6, 1, 7, 0, 0, TimeSpan.Zero), model.NextRunUtc);
        Assert.Contains("09:00", model.NextRunLocal);
        Assert.Equal("Test/Plus2", model.NextRunLocalLabel);
    }

    [Fact]
    public void Create_validation_error_returns_parser_message()
    {
        var store = new ScheduledTaskStore();
        var svc = new ScheduleControlService(store, runtimeView: null);

        // Zero selectors — parser says "requires exactly one of..."
        var result = svc.Create(new ScheduleCreateRequest(null, "do", null, null, null, null));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("exactly one", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Create_blank_prompt_returns_error()
    {
        var store = new ScheduledTaskStore();
        var svc = new ScheduleControlService(store, runtimeView: null);

        var result = svc.Create(new ScheduleCreateRequest(null, "   ", Every: "5m", null, null, null));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Create_invalid_cron_returns_parser_error()
    {
        var store = new ScheduledTaskStore();
        var svc = new ScheduleControlService(store, runtimeView: null, new FixedTimeProvider(Epoch));

        var result = svc.Create(new ScheduleCreateRequest(null, "x", null, null, Cron: "BADCRON", null));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Create_null_store_with_invalid_input_returns_parser_error_not_no_store()
    {
        // Parity with original ScheduleCreateTool behavior: parse runs first so invalid input
        // produces the parser's validation message even when no store is available.
        var svc = new ScheduleControlService(null, runtimeView: null);

        // Zero selectors — parser says "requires exactly one of..."
        var result = svc.Create(new ScheduleCreateRequest(null, "ping", null, null, null, null));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("exactly one", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No schedule store", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_null_store_returns_no_store_error()
    {
        var svc = new ScheduleControlService(null, runtimeView: null);

        var result = svc.Create(new ScheduleCreateRequest(null, "ping", Every: "5m", null, null, null));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("No schedule store", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_existing_id_returns_true_and_removes_definition()
    {
        var store = SeedInterval(out var id);
        var svc = new ScheduleControlService(store, runtimeView: null);

        var found = svc.Delete(id);

        Assert.True(found);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Delete_unknown_id_returns_false_and_leaves_store_unchanged()
    {
        var store = SeedInterval(out _);
        var svc = new ScheduleControlService(store, runtimeView: null);

        var found = svc.Delete("no-such-id");

        Assert.False(found);
        Assert.Single(store.Items);
    }

    [Fact]
    public void Delete_null_store_returns_false()
    {
        var svc = new ScheduleControlService(null, runtimeView: null);
        Assert.False(svc.Delete("any-id"));
    }

    // ── Parity: tool and service produce identical persisted definitions ──────

    [Fact]
    public async Task Tool_and_service_Create_produce_identical_persisted_definition()
    {
        var now = new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var tp = new FixedTimeProvider(now);

        // Tool path:
        var toolStore = new ScheduledTaskStore();
        var tool = new ScheduleCreateTool(tp);
        var ctx = new ToolContext(".") { Schedules = toolStore };
        await tool.ExecuteAsync(
            JsonDocument.Parse("""{"prompt":"check logs","every":"15m"}""").RootElement, ctx);
        var toolTask = Assert.Single(toolStore.Items);

        // Service path (same time/zone seams):
        var svcStore = new ScheduledTaskStore();
        var svc = new ScheduleControlService(svcStore, runtimeView: null, tp);
        var result = svc.Create(new ScheduleCreateRequest(null, "check logs", Every: "15m", null, null, null));
        Assert.True(result.IsSuccess);
        var svcTask = Assert.Single(svcStore.Items);

        // Observable definition fields must match (ids differ; timestamps are deterministic).
        Assert.Equal(toolTask.Kind, svcTask.Kind);
        Assert.Equal(toolTask.Interval, svcTask.Interval);
        Assert.Equal(toolTask.Prompt, svcTask.Prompt);
        Assert.Equal(toolTask.TimeZoneId, svcTask.TimeZoneId);
        Assert.Equal(toolTask.NextRunUtc, svcTask.NextRunUtc);
        Assert.Equal(toolTask.Name, svcTask.Name);
        Assert.Equal(toolTask.Cron, svcTask.Cron);
        Assert.Null(toolTask.LastTerminalOutcome);
        Assert.Null(svcTask.LastTerminalOutcome);
    }
}
