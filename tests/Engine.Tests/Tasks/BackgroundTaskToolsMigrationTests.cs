using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using LlmClient;
using Xunit;

namespace Engine.Tests.Tasks;

/// <summary>
/// Behavioural coverage for the <c>task_start</c>/<c>task_output</c>/<c>task_stop</c> tools after
/// they were migrated off the legacy <c>BackgroundTaskRunner</c> onto <see cref="TaskManager"/>.
/// These lock the exact legacy-compatible messages, the incremental cursor semantics, the
/// truncation notice, every terminal status label, and the missing-context / missing-input /
/// unknown-id branches so the runner can be deleted without losing tool coverage.
/// </summary>
public sealed class BackgroundTaskToolsMigrationTests
{
    private static TaskManager NewManager(long? outputRingBytes = null) =>
        outputRingBytes is { } bytes
            ? new TaskManager(sessionId: "mig", logRoot: null, outputRingBytes: bytes)
            : new TaskManager(sessionId: "mig", logRoot: null);

    private static ToolContext Context(
        TaskManager? tasks,
        ISubagentHost? host = null,
        string? currentTaskId = null,
        int currentDepth = 0) =>
        new(WorkingDirectory: ".")
        {
            Tasks = tasks,
            Subagents = host,
            CurrentTaskId = currentTaskId,
            CurrentDepth = currentDepth,
        };

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    /// <summary>A host that writes output then blocks until an optional gate is released.</summary>
    private sealed class FakeHost : ISubagentHost
    {
        private readonly string _output;
        private readonly TaskCompletionSource? _gate;

        public FakeHost(string output, TaskCompletionSource? gate = null)
        {
            _output = output;
            _gate = gate;
        }

        public async Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default)
        {
            sink.OnAssistantText(_output);
            sink.OnAssistantTextComplete();
            if (_gate is not null)
            {
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return _output;
        }
    }

    private static async Task WaitForStatus(TaskManager mgr, string id, TaskRunStatus target)
    {
        for (var i = 0; i < 200; i++)
        {
            if (mgr.Get(id)?.Status == target) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"Task {id} did not reach {target} in time.");
    }

    // ─── task_start ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_missing_task_manager_reports_not_available()
    {
        var tool = new BackgroundTaskStartTool();
        var context = Context(tasks: null, host: new FakeHost("out"));

        var result = await tool.ExecuteAsync(Json("""{"prompt":"go"}"""), context);

        Assert.False(result.IsError);
        Assert.Equal("Background tasks are not available in this context.", result.Content);
    }

    [Fact]
    public async Task Start_missing_subagent_host_reports_not_available()
    {
        using var mgr = NewManager();
        var tool = new BackgroundTaskStartTool();
        var context = Context(tasks: mgr, host: null);

        var result = await tool.ExecuteAsync(Json("""{"prompt":"go"}"""), context);

        Assert.False(result.IsError);
        Assert.Equal("Background tasks are not available in this context.", result.Content);
    }

    [Fact]
    public async Task Start_missing_prompt_returns_error()
    {
        using var mgr = NewManager();
        var tool = new BackgroundTaskStartTool();
        var context = Context(tasks: mgr, host: new FakeHost("out"));

        var result = await tool.ExecuteAsync(Json("""{}"""), context);

        Assert.True(result.IsError);
        Assert.Equal("Missing required 'prompt'.", result.Content);
    }

    [Fact]
    public async Task Start_at_max_depth_is_rejected()
    {
        using var mgr = NewManager();
        var tool = new BackgroundTaskStartTool();
        var context = Context(tasks: mgr, host: new FakeHost("out"), currentDepth: TaskManager.MaxSubagentDepth);

        var result = await tool.ExecuteAsync(Json("""{"prompt":"go"}"""), context);

        Assert.True(result.IsError);
        Assert.Equal(
            "Cannot start a background subagent from here: the maximum subagent nesting depth has been reached.",
            result.Content);
        Assert.Empty(mgr.List());
    }

    [Fact]
    public async Task Start_returns_id_registers_task_and_completes()
    {
        using var mgr = NewManager();
        var tool = new BackgroundTaskStartTool();
        var context = Context(tasks: mgr, host: new FakeHost("subagent output"));

        var result = await tool.ExecuteAsync(Json("""{"prompt":"go"}"""), context);

        Assert.False(result.IsError);
        Assert.Equal("Started background task task-0001. Use task_output to read its progress.", result.Content);

        var entry = Assert.Single(mgr.List());
        Assert.Equal("task-0001", entry.Id);

        await WaitForStatus(mgr, "task-0001", TaskRunStatus.Completed);
    }

    // ─── task_output ────────────────────────────────────────────────────────

    [Fact]
    public async Task Output_missing_task_manager_reports_not_available()
    {
        var tool = new BackgroundTaskOutputTool();
        var context = Context(tasks: null);

        var result = await tool.ExecuteAsync(Json("""{"task_id":"task-0001"}"""), context);

        Assert.False(result.IsError);
        Assert.Equal("Background tasks are not available in this context.", result.Content);
    }

    [Fact]
    public async Task Output_missing_task_id_returns_error()
    {
        using var mgr = NewManager();
        var tool = new BackgroundTaskOutputTool();

        var result = await tool.ExecuteAsync(Json("""{}"""), Context(tasks: mgr));

        Assert.True(result.IsError);
        Assert.Equal("Missing required 'task_id'.", result.Content);
    }

    [Fact]
    public async Task Output_unknown_id_reports_not_found()
    {
        using var mgr = NewManager();
        var tool = new BackgroundTaskOutputTool();

        var result = await tool.ExecuteAsync(Json("""{"task_id":"ghost"}"""), Context(tasks: mgr));

        Assert.False(result.IsError);
        Assert.Equal("Task 'ghost' not found.", result.Content);
    }

    [Fact]
    public async Task Output_reads_new_text_then_reports_no_new_output_incrementally()
    {
        using var mgr = NewManager();
        var task = mgr.Register(TaskKind.Subagent, "d", parentTaskId: null);
        mgr.AppendOutput(task.Id, "hello world");
        var tool = new BackgroundTaskOutputTool();
        var input = Json($$"""{"task_id":"{{task.Id}}"}""");

        var first = await tool.ExecuteAsync(input, Context(tasks: mgr));
        Assert.Equal("hello world\n[status: running]", first.Content);

        // Cursor advanced; nothing new appended and the task is still running.
        var second = await tool.ExecuteAsync(input, Context(tasks: mgr));
        Assert.Equal("(no new output yet; still running)\n[status: running]", second.Content);
    }

    [Fact]
    public async Task Output_no_new_output_on_finished_task_uses_since_last_read_wording()
    {
        using var mgr = NewManager();
        var task = mgr.Register(TaskKind.Subagent, "d", parentTaskId: null);
        mgr.AppendOutput(task.Id, "partial");
        var tool = new BackgroundTaskOutputTool();
        var input = Json($$"""{"task_id":"{{task.Id}}"}""");

        await tool.ExecuteAsync(input, Context(tasks: mgr)); // drain the ring
        mgr.Complete(task.Id, "done");

        var result = await tool.ExecuteAsync(input, Context(tasks: mgr));
        Assert.Equal("(no new output since last read)\n[status: completed]", result.Content);
    }

    [Fact]
    public async Task Output_prepends_truncation_notice_when_output_evicted()
    {
        // A tiny ring so a large append evicts everything before the main cursor.
        using var mgr = NewManager(outputRingBytes: 32);
        var task = mgr.Register(TaskKind.Subagent, "d", parentTaskId: null);
        var tool = new BackgroundTaskOutputTool();
        var input = Json($$"""{"task_id":"{{task.Id}}"}""");

        mgr.AppendOutput(task.Id, "AAAA");
        await tool.ExecuteAsync(input, Context(tasks: mgr)); // advance cursor past "AAAA"

        mgr.AppendOutput(task.Id, new string('B', 200)); // evicts past the cursor

        var result = await tool.ExecuteAsync(input, Context(tasks: mgr));
        Assert.StartsWith("[earlier output truncated]\n", result.Content);
        Assert.EndsWith("\n[status: running]", result.Content);
    }

    [Theory]
    [InlineData(TaskRunStatus.Running, "running")]
    [InlineData(TaskRunStatus.Completed, "completed")]
    [InlineData(TaskRunStatus.Failed, "failed")]
    [InlineData(TaskRunStatus.Stopped, "stopped")]
    public async Task Output_status_label_maps_every_terminal_state(TaskRunStatus status, string label)
    {
        using var mgr = NewManager();
        var task = mgr.Register(TaskKind.Subagent, "d", parentTaskId: null);
        switch (status)
        {
            case TaskRunStatus.Completed: mgr.Complete(task.Id, "done"); break;
            case TaskRunStatus.Failed: mgr.Fail(task.Id, "boom"); break;
            case TaskRunStatus.Stopped: mgr.Stop(task.Id); break;
            case TaskRunStatus.Running: break;
        }

        var tool = new BackgroundTaskOutputTool();
        var result = await tool.ExecuteAsync(Json($$"""{"task_id":"{{task.Id}}"}"""), Context(tasks: mgr));

        Assert.EndsWith($"[status: {label}]", result.Content);
    }

    // ─── task_stop ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_missing_task_manager_reports_not_available()
    {
        var tool = new BackgroundTaskStopTool();

        var result = await tool.ExecuteAsync(Json("""{"task_id":"task-0001"}"""), Context(tasks: null));

        Assert.False(result.IsError);
        Assert.Equal("Background tasks are not available in this context.", result.Content);
    }

    [Fact]
    public async Task Stop_missing_task_id_returns_error()
    {
        using var mgr = NewManager();
        var tool = new BackgroundTaskStopTool();

        var result = await tool.ExecuteAsync(Json("""{}"""), Context(tasks: mgr));

        Assert.True(result.IsError);
        Assert.Equal("Missing required 'task_id'.", result.Content);
    }

    [Fact]
    public async Task Stop_unknown_id_reports_not_found()
    {
        using var mgr = NewManager();
        var tool = new BackgroundTaskStopTool();

        var result = await tool.ExecuteAsync(Json("""{"task_id":"ghost"}"""), Context(tasks: mgr));

        Assert.False(result.IsError);
        Assert.Equal("Task 'ghost' not found.", result.Content);
    }

    [Fact]
    public async Task Stop_running_task_reports_stopped()
    {
        using var mgr = NewManager();
        var task = mgr.Register(TaskKind.Subagent, "d", parentTaskId: null);
        var tool = new BackgroundTaskStopTool();

        var result = await tool.ExecuteAsync(Json($$"""{"task_id":"{{task.Id}}"}"""), Context(tasks: mgr));

        Assert.False(result.IsError);
        Assert.Equal($"Task '{task.Id}' has been stopped.", result.Content);
        Assert.True(mgr.Find(task.Id)!.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Stop_finished_task_reports_already_finished()
    {
        using var mgr = NewManager();
        var task = mgr.Register(TaskKind.Subagent, "d", parentTaskId: null);
        mgr.Complete(task.Id, "done");
        var tool = new BackgroundTaskStopTool();

        var result = await tool.ExecuteAsync(Json($$"""{"task_id":"{{task.Id}}"}"""), Context(tasks: mgr));

        Assert.False(result.IsError);
        Assert.Equal($"Task '{task.Id}' is already finished and cannot be stopped.", result.Content);
    }

    // ─── caller-scoped isolation (context.CurrentTaskId flows to the manager) ─

    [Fact]
    public async Task Stop_denied_when_target_outside_callers_subtree_looks_like_not_found()
    {
        using var mgr = NewManager();
        var a = mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        var b = mgr.Register(TaskKind.Subagent, "b", parentTaskId: null);
        var tool = new BackgroundTaskStopTool();

        // Caller task "a" tries to stop unrelated task "b": the denial is reported with the same
        // not-found wording so the caller cannot probe existence, and "b" keeps running.
        var context = Context(tasks: mgr, currentTaskId: a.Id, currentDepth: 1);
        var result = await tool.ExecuteAsync(Json($$"""{"task_id":"{{b.Id}}"}"""), context);

        Assert.False(result.IsError);
        Assert.Equal($"Task '{b.Id}' not found.", result.Content);
        Assert.Equal(TaskRunStatus.Running, mgr.Get(b.Id)!.Status);
    }

    [Fact]
    public async Task Output_denied_when_target_outside_callers_subtree_looks_like_not_found()
    {
        using var mgr = NewManager();
        var a = mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        var b = mgr.Register(TaskKind.Subagent, "b", parentTaskId: null);
        mgr.AppendOutput(b.Id, "secret");
        var tool = new BackgroundTaskOutputTool();

        var context = Context(tasks: mgr, currentTaskId: a.Id, currentDepth: 1);
        var result = await tool.ExecuteAsync(Json($$"""{"task_id":"{{b.Id}}"}"""), context);

        Assert.False(result.IsError);
        Assert.Equal($"Task '{b.Id}' not found.", result.Content);
        Assert.DoesNotContain("secret", result.Content);
    }

    [Fact]
    public async Task Output_allowed_for_callers_own_descendant()
    {
        using var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "p", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "c", parentTaskId: parent.Id);
        mgr.AppendOutput(child.Id, "child progress");
        var tool = new BackgroundTaskOutputTool();

        var context = Context(tasks: mgr, currentTaskId: parent.Id, currentDepth: 1);
        var result = await tool.ExecuteAsync(Json($$"""{"task_id":"{{child.Id}}"}"""), context);

        Assert.False(result.IsError);
        Assert.Equal("child progress\n[status: running]", result.Content);
    }
}
