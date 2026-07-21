using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Scheduling;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using LlmClient;

namespace Engine.Tests.Scheduling;

/// <summary>
/// Behavioural coverage for the redesigned schedule tools: the every/at/cron create contract,
/// the enriched list output (runtime state, active task id, terminal metadata, timezone fallback),
/// the soft delete semantics, and the runtime-view threading through the tool pipeline.
/// </summary>
public sealed class ScheduleToolTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static readonly DateTimeOffset Epoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

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

    // ── schedule_create: schema ─────────────────────────────────────────────

    [Fact]
    public void Create_schema_declares_selectors_and_excludes_recurring()
    {
        var tool = new ScheduleCreateTool();
        using var doc = JsonDocument.Parse(tool.InputSchemaJson);
        var props = doc.RootElement.GetProperty("properties");

        Assert.True(props.TryGetProperty("name", out _));
        Assert.True(props.TryGetProperty("prompt", out _));
        Assert.True(props.TryGetProperty("every", out _));
        Assert.True(props.TryGetProperty("at", out _));
        Assert.True(props.TryGetProperty("cron", out _));
        Assert.True(props.TryGetProperty("timeZone", out _));
        Assert.False(props.TryGetProperty("recurring", out _));

        var required = doc.RootElement.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("prompt", required);
        Assert.DoesNotContain("cron", required);

        Assert.DoesNotContain("recurring", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly one", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    // ── schedule_create: validation ─────────────────────────────────────────

    [Fact]
    public async Task Create_requires_exactly_one_selector()
    {
        var store = new ScheduledTaskStore();
        var tool = new ScheduleCreateTool();
        var ctx = new ToolContext(".") { Schedules = store };

        var zero = await tool.ExecuteAsync(Json("""{"prompt":"do"}"""), ctx);
        Assert.True(zero.IsError);

        var two = await tool.ExecuteAsync(
            Json("""{"prompt":"do","every":"3m","cron":"* * * * *"}"""), ctx);
        Assert.True(two.IsError);

        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task Create_blank_prompt_is_error()
    {
        var store = new ScheduledTaskStore();
        var tool = new ScheduleCreateTool();
        var ctx = new ToolContext(".") { Schedules = store };

        var r = await tool.ExecuteAsync(Json("""{"prompt":"   ","every":"3m"}"""), ctx);

        Assert.True(r.IsError);
        Assert.Empty(store.Items);
    }

    // ── schedule_create: every ──────────────────────────────────────────────

    [Fact]
    public async Task Create_every_uses_injected_time_and_makes_interval()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var store = new ScheduledTaskStore();
        var tool = new ScheduleCreateTool(new FixedTimeProvider(now));
        var ctx = new ToolContext(".") { Schedules = store };

        var r = await tool.ExecuteAsync(Json("""{"prompt":"ping","every":"3m"}"""), ctx);

        Assert.False(r.IsError);
        var item = Assert.Single(store.Items);
        Assert.Equal(ScheduleKind.Interval, item.Kind);
        Assert.Equal(TimeSpan.FromMinutes(3), item.Interval);
        Assert.Equal(now.AddMinutes(3), item.NextRunUtc);
        Assert.Contains(item.Id, r.Content);
        Assert.Contains("2025-01-01 12:03", r.Content);
    }

    // ── schedule_create: at ─────────────────────────────────────────────────

    [Fact]
    public async Task Create_at_local_converts_using_injected_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test/Plus2", TimeSpan.FromHours(2), "Test/Plus2", "Test/Plus2");
        var store = new ScheduledTaskStore();
        var tool = new ScheduleCreateTool(new FixedTimeProvider(Epoch), () => zone);
        var ctx = new ToolContext(".") { Schedules = store };

        var r = await tool.ExecuteAsync(
            Json("""{"prompt":"wake","at":"2025-06-01T09:00:00"}"""), ctx);

        Assert.False(r.IsError);
        var item = Assert.Single(store.Items);
        Assert.Equal(ScheduleKind.At, item.Kind);
        Assert.Equal(new DateTimeOffset(2025, 6, 1, 7, 0, 0, TimeSpan.Zero), item.NextRunUtc);
        Assert.Equal("Test/Plus2", item.TimeZoneId);
        Assert.Contains("07:00", r.Content);
        Assert.Contains("09:00", r.Content);
    }

    [Fact]
    public async Task Create_at_explicit_offset_output()
    {
        var store = new ScheduledTaskStore();
        var tool = new ScheduleCreateTool(new FixedTimeProvider(Epoch));
        var ctx = new ToolContext(".") { Schedules = store };

        var r = await tool.ExecuteAsync(
            Json("""{"prompt":"wake","at":"2025-06-01T09:00:00+05:00"}"""), ctx);

        Assert.False(r.IsError);
        var item = Assert.Single(store.Items);
        Assert.Equal(ScheduleKind.At, item.Kind);
        Assert.Equal(new DateTimeOffset(2025, 6, 1, 4, 0, 0, TimeSpan.Zero), item.NextRunUtc);
        Assert.Equal("UTC+05:00", item.TimeZoneId);
        Assert.Contains("UTC+05:00", r.Content);
        Assert.Contains("04:00", r.Content);
    }

    // ── schedule_create: cron ───────────────────────────────────────────────

    [Fact]
    public async Task Create_cron_outputs_normalized_rule_and_timezone()
    {
        var store = new ScheduledTaskStore();
        var tool = new ScheduleCreateTool(new FixedTimeProvider(Epoch));
        var ctx = new ToolContext(".") { Schedules = store };

        var r = await tool.ExecuteAsync(
            Json("""{"prompt":"nightly","cron":"0 0 * * *","timeZone":"UTC"}"""), ctx);

        Assert.False(r.IsError);
        var item = Assert.Single(store.Items);
        Assert.Equal(ScheduleKind.Cron, item.Kind);
        Assert.Equal("UTC", item.TimeZoneId);
        Assert.NotNull(item.Cron);
        Assert.Contains(item.Cron!, r.Content);
        Assert.Contains("UTC", r.Content);
    }

    // ── schedule_create: null store ─────────────────────────────────────────

    [Fact]
    public async Task Create_null_store_is_error_and_does_not_claim_creation()
    {
        var tool = new ScheduleCreateTool();
        var ctx = new ToolContext(".");

        var r = await tool.ExecuteAsync(Json("""{"prompt":"do","every":"5m"}"""), ctx);

        Assert.True(r.IsError);
        Assert.DoesNotContain("created", r.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_is_readonly_and_named()
    {
        var tool = new ScheduleCreateTool();
        Assert.Equal("schedule_create", tool.Name);
        Assert.True(tool.IsReadOnly);
    }

    // ── schedule_list: runtime state ────────────────────────────────────────

    [Fact]
    public async Task List_defaults_to_idle_without_runtime()
    {
        var store = SeedInterval(out _);
        var tool = new ScheduleListTool();
        var ctx = new ToolContext(".") { Schedules = store };

        var r = await tool.ExecuteAsync(Json("{}"), ctx);

        Assert.False(r.IsError);
        Assert.Contains("idle", r.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hello world prompt", r.Content);
    }

    [Fact]
    public async Task List_reports_running_and_active_task_from_runtime()
    {
        var store = SeedInterval(out var id);
        var view = new FakeRuntimeView(new ScheduleRuntimeState(ScheduleRuntimeStatus.Running, "task-77"), id);
        var tool = new ScheduleListTool();
        var ctx = new ToolContext(".") { Schedules = store, ScheduleRuntime = view };

        var r = await tool.ExecuteAsync(Json("{}"), ctx);

        Assert.False(r.IsError);
        Assert.Contains("running", r.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("task-77", r.Content);
    }

    [Fact]
    public async Task List_reports_pending_from_runtime()
    {
        var store = SeedInterval(out var id);
        var view = new FakeRuntimeView(new ScheduleRuntimeState(ScheduleRuntimeStatus.Pending, null), id);
        var tool = new ScheduleListTool();
        var ctx = new ToolContext(".") { Schedules = store, ScheduleRuntime = view };

        var r = await tool.ExecuteAsync(Json("{}"), ctx);

        Assert.False(r.IsError);
        Assert.Contains("pending", r.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_shows_last_terminal_outcome()
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

        var tool = new ScheduleListTool();
        var ctx = new ToolContext(".") { Schedules = store };
        var r = await tool.ExecuteAsync(Json("{}"), ctx);

        Assert.False(r.IsError);
        Assert.Contains("Failed", r.Content);
        Assert.Contains("boom", r.Content);
    }

    [Fact]
    public async Task List_invalid_timezone_falls_back_to_utc_without_throwing()
    {
        var store = new ScheduledTaskStore();
        var draft = new ScheduleDefinitionDraft(
            null, ScheduleKind.Cron, "x", null, null, "0 0 * * *", "Not/A_Zone",
            new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero));
        store.Add(draft, Epoch);

        var tool = new ScheduleListTool();
        var ctx = new ToolContext(".") { Schedules = store };

        var r = await tool.ExecuteAsync(Json("{}"), ctx);

        Assert.False(r.IsError);
        Assert.Contains("UTC", r.Content);
    }

    [Fact]
    public async Task List_empty_store_message()
    {
        var tool = new ScheduleListTool();
        var ctx = new ToolContext(".") { Schedules = new ScheduledTaskStore() };
        var r = await tool.ExecuteAsync(Json("{}"), ctx);

        Assert.False(r.IsError);
        Assert.Contains("No scheduled tasks", r.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ── schedule_delete ─────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_unknown_id_is_soft_and_leaves_tasks_untouched()
    {
        using var tasks = new TaskManager(sessionId: "d", logRoot: null);
        tasks.Register(TaskKind.Subagent, "active", parentTaskId: null);
        var store = new ScheduledTaskStore();
        var tool = new ScheduleDeleteTool();
        var ctx = new ToolContext(".") { Schedules = store, Tasks = tasks };

        var r = await tool.ExecuteAsync(Json("""{"id":"nope"}"""), ctx);

        Assert.False(r.IsError);
        Assert.Contains("not found", r.Content, StringComparison.OrdinalIgnoreCase);
        var entry = Assert.Single(tasks.List());
        Assert.Equal(TaskRunStatus.Running, entry.Status);
    }

    [Fact]
    public async Task Delete_removes_definition_and_notes_running_continues()
    {
        var store = SeedInterval(out var id);
        var tool = new ScheduleDeleteTool();
        var ctx = new ToolContext(".") { Schedules = store };

        var r = await tool.ExecuteAsync(Json($$"""{"id":"{{id}}"}"""), ctx);

        Assert.False(r.IsError);
        Assert.Empty(store.Items);
        Assert.Contains("continue", r.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ── pipeline threading ──────────────────────────────────────────────────

    [Fact]
    public async Task AgentLoop_threads_runtime_view_into_tool_context()
    {
        var view = new FakeRuntimeView(new ScheduleRuntimeState(ScheduleRuntimeStatus.Idle, null));
        var capture = new CaptureContextTool();

        var toolTurn = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("t1", capture.Name, "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var endTurn = new[] { AssistantStreamEvent.Delta("done"), AssistantStreamEvent.Finished("end_turn") };

        var loop = new AgentLoop(
            new ScriptedClient(toolTurn, endTurn),
            new ToolRegistry([capture]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" },
            scheduleRuntime: view);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.Same(view, capture.Captured);
    }

    private sealed class CaptureContextTool : ITool
    {
        public IScheduleRuntimeView? Captured { get; private set; }

        public string Name => "capture_ctx";

        public string Description => "captures the tool context runtime view";

        public string InputSchemaJson => """{"type":"object","properties":{}}""";

        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(
            JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            this.Captured = context.ScheduleRuntime;
            return Task.FromResult(new ToolResult("ok"));
        }
    }

    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = turns[Math.Min(this.turn, turns.Length - 1)];
            this.turn++;
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }

        public void OnAssistantTextComplete() { }

        public void OnToolCall(string toolName, string inputPreview) { }

        public void OnToolResult(string toolName, ToolResult result) { }

        public void OnError(string message) { }
    }
}
