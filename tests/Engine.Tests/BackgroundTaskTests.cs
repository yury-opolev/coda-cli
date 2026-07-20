using System.Text.Json;
using Coda.Agent;
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Tools;
using LlmClient;

namespace Engine.Tests;

public sealed class BackgroundTaskTests
{
    // ─── Fake helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// A controllable fake ISubagentHost whose RunSubagentAsync writes text to the
    /// passed sink then (optionally) waits for a gate before completing.
    /// </summary>
    private sealed class FakeSubagentHost : ISubagentHost
    {
        private readonly string outputText;
        private readonly TaskCompletionSource? gate;

        public FakeSubagentHost(string outputText, TaskCompletionSource? gate = null)
        {
            this.outputText = outputText;
            this.gate = gate;
        }

        public async Task<string> RunSubagentAsync(
            string subagentType,
            string prompt,
            IAgentSink parentSink,
            SteeringInbox steering,
            string taskId,
            int depth,
            CancellationToken cancellationToken = default)
        {
            parentSink.OnAssistantText(this.outputText);
            parentSink.OnAssistantTextComplete();

            if (this.gate is not null)
            {
                await this.gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return this.outputText;
        }
    }

    /// <summary>
    /// A fake ISubagentHost that honours cancellation: blocks until cancelled,
    /// writing output before waiting.
    /// </summary>
    private sealed class CancellableFakeHost : ISubagentHost
    {
        private readonly string outputText;

        public CancellableFakeHost(string outputText)
        {
            this.outputText = outputText;
        }

        public async Task<string> RunSubagentAsync(
            string subagentType,
            string prompt,
            IAgentSink parentSink,
            SteeringInbox steering,
            string taskId,
            int depth,
            CancellationToken cancellationToken = default)
        {
            parentSink.OnAssistantText(this.outputText);
            parentSink.OnAssistantTextComplete();

            // Wait until the token is cancelled.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            return this.outputText;
        }
    }

    private static ToolContext MakeContext(BackgroundTaskRunner runner, ISubagentHost host) =>
        new(WorkingDirectory: ".")
        {
            BackgroundTasks = runner,
            Subagents = host,
        };

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ─── BackgroundTask unit tests ───────────────────────────────────────────────

    [Fact]
    public void BackgroundTask_ReadFromCursor_returns_all_appended_text()
    {
        var task = new BackgroundTask("t1", new CancellationTokenSource());
        task.Append("hello ");
        task.Append("world");

        var (text, status) = task.ReadFromCursor();

        Assert.Equal("hello world", text);
        Assert.Equal(BackgroundTaskStatus.Running, status);
    }

    [Fact]
    public void BackgroundTask_ReadFromCursor_is_incremental()
    {
        var task = new BackgroundTask("t1", new CancellationTokenSource());
        task.Append("first");
        task.ReadFromCursor(); // advance cursor

        task.Append(" second");
        var (text, _) = task.ReadFromCursor();

        Assert.Equal(" second", text);
    }

    [Fact]
    public void BackgroundTask_MarkCompleted_sets_status_and_result()
    {
        var task = new BackgroundTask("t1", new CancellationTokenSource());
        task.MarkCompleted("final result");

        var (_, status) = task.ReadFromCursor();

        Assert.Equal(BackgroundTaskStatus.Completed, status);
        Assert.Equal("final result", task.FinalResult);
    }

    [Fact]
    public void BackgroundTask_MarkFailed_sets_status_and_error()
    {
        var task = new BackgroundTask("t1", new CancellationTokenSource());
        task.MarkFailed("boom");

        var (_, status) = task.ReadFromCursor();

        Assert.Equal(BackgroundTaskStatus.Failed, status);
        Assert.Equal("boom", task.ErrorMessage);
    }

    [Fact]
    public void BackgroundTask_MarkStopped_sets_status_stopped()
    {
        var task = new BackgroundTask("t1", new CancellationTokenSource());
        task.MarkStopped();

        var (_, status) = task.ReadFromCursor();

        Assert.Equal(BackgroundTaskStatus.Stopped, status);
    }

    [Fact]
    public void BackgroundTask_Cancel_requests_cancellation()
    {
        var cts = new CancellationTokenSource();
        var task = new BackgroundTask("t1", cts);
        task.Cancel();

        Assert.True(task.Token.IsCancellationRequested);
    }

    // ─── BackgroundTaskRunner unit tests ────────────────────────────────────────

    [Fact]
    public async Task Runner_Start_returns_id_and_task_completes()
    {
        var runner = new BackgroundTaskRunner();
        var host = new FakeSubagentHost("the output");

        var id = runner.Start(host, "general-purpose", "do something");

        Assert.False(string.IsNullOrWhiteSpace(id));

        // Poll until the task completes (bounded to avoid hanging tests).
        BackgroundTaskStatus status;
        var attempts = 0;
        do
        {
            await Task.Delay(10);
            var (_, s) = runner.Read(id);
            status = s;
            attempts++;
        }
        while (status == BackgroundTaskStatus.Running && attempts < 200);

        Assert.Equal(BackgroundTaskStatus.Completed, status);
    }

    [Fact]
    public async Task Runner_Read_returns_captured_output_and_transitions_to_completed()
    {
        var runner = new BackgroundTaskRunner();
        var host = new FakeSubagentHost("hello from subagent");

        var id = runner.Start(host, "general-purpose", "go");

        // Poll until Completed.
        string allOutput = string.Empty;
        var attempts = 0;
        BackgroundTaskStatus status;
        do
        {
            await Task.Delay(10);
            var (newText, s) = runner.Read(id);
            allOutput += newText;
            status = s;
            attempts++;
        }
        while (status == BackgroundTaskStatus.Running && attempts < 200);

        Assert.Equal(BackgroundTaskStatus.Completed, status);
        Assert.Contains("hello from subagent", allOutput);
    }

    [Fact]
    public async Task Runner_Read_is_incremental_returns_only_new_text_on_second_call()
    {
        // Gate that we control — keeps subagent alive while we take two reads.
        var gate = new TaskCompletionSource();
        var runner = new BackgroundTaskRunner();
        var host = new FakeSubagentHost("chunk1", gate);

        var id = runner.Start(host, "general-purpose", "go");

        // Wait until the captured sink has received "chunk1".
        string firstRead = string.Empty;
        var attempts = 0;
        while (firstRead.Length == 0 && attempts < 200)
        {
            await Task.Delay(10);
            var (text, _) = runner.Read(id);
            firstRead += text;
            attempts++;
        }

        // Now the cursor is advanced. Release the gate to let subagent complete.
        gate.SetResult();

        // Poll until task completes, collecting only NEW text from second read onwards.
        string secondRead = string.Empty;
        attempts = 0;
        BackgroundTaskStatus status;
        do
        {
            await Task.Delay(10);
            var (text, s) = runner.Read(id);
            secondRead += text;
            status = s;
            attempts++;
        }
        while (status == BackgroundTaskStatus.Running && attempts < 200);

        // first read captured text; second read must NOT re-include it.
        Assert.DoesNotContain("chunk1", secondRead);
    }

    [Fact]
    public async Task Runner_Stop_cancels_running_task_and_status_becomes_stopped()
    {
        var runner = new BackgroundTaskRunner();
        var host = new CancellableFakeHost("partial output");

        var id = runner.Start(host, "general-purpose", "go");

        // Wait until output arrives (confirms host started).
        var attempts = 0;
        string captured = string.Empty;
        while (captured.Length == 0 && attempts < 200)
        {
            await Task.Delay(10);
            var (text, _) = runner.Read(id);
            captured += text;
            attempts++;
        }

        var found = runner.Stop(id);
        Assert.True(found);

        // Poll until Stopped.
        BackgroundTaskStatus status;
        attempts = 0;
        do
        {
            await Task.Delay(10);
            var (_, s) = runner.Read(id);
            status = s;
            attempts++;
        }
        while (status == BackgroundTaskStatus.Running && attempts < 200);

        Assert.Equal(BackgroundTaskStatus.Stopped, status);
    }

    [Fact]
    public void Runner_Read_unknown_id_returns_not_found()
    {
        var runner = new BackgroundTaskRunner();

        var (found, _, _) = runner.ReadFull("nonexistent");

        Assert.False(found);
    }

    [Fact]
    public void Runner_Stop_unknown_id_returns_false()
    {
        var runner = new BackgroundTaskRunner();

        var found = runner.Stop("nonexistent");

        Assert.False(found);
    }

    [Fact]
    public async Task Runner_List_shows_all_tasks()
    {
        var runner = new BackgroundTaskRunner();
        var gate = new TaskCompletionSource();
        var host1 = new FakeSubagentHost("a", gate);
        var host2 = new FakeSubagentHost("b", gate);

        var id1 = runner.Start(host1, "general-purpose", "task1");
        var id2 = runner.Start(host2, "general-purpose", "task2");

        var list = runner.List();

        Assert.Contains(list, item => item.Id == id1);
        Assert.Contains(list, item => item.Id == id2);

        gate.SetResult(); // clean up
        await Task.Delay(50);
    }

    // ─── CapturingSink tests ─────────────────────────────────────────────────────

    [Fact]
    public void CapturingSink_OnAssistantText_appends_to_task_buffer()
    {
        var cts = new CancellationTokenSource();
        var task = new BackgroundTask("t1", cts);
        var sink = new CapturingSink(task);

        sink.OnAssistantText("hello");
        sink.OnAssistantText(" world");

        var (text, _) = task.ReadFromCursor();
        Assert.Equal("hello world", text);
    }

    [Fact]
    public void CapturingSink_OnToolCall_appends_tool_marker()
    {
        var cts = new CancellationTokenSource();
        var task = new BackgroundTask("t1", cts);
        var sink = new CapturingSink(task);

        sink.OnToolCall("read_file", "{}");

        var (text, _) = task.ReadFromCursor();
        Assert.Contains("[tool: read_file]", text);
    }

    [Fact]
    public void CapturingSink_OnToolResult_appends_result_marker()
    {
        var cts = new CancellationTokenSource();
        var task = new BackgroundTask("t1", cts);
        var sink = new CapturingSink(task);

        sink.OnToolResult("read_file", new ToolResult("content"));

        var (text, _) = task.ReadFromCursor();
        Assert.NotEmpty(text);
    }

    [Fact]
    public void CapturingSink_OnError_appends_error_marker()
    {
        var cts = new CancellationTokenSource();
        var task = new BackgroundTask("t1", cts);
        var sink = new CapturingSink(task);

        sink.OnError("something failed");

        var (text, _) = task.ReadFromCursor();
        Assert.Contains("[error:", text);
        Assert.Contains("something failed", text);
    }

    // ─── Tool tests ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TaskStartTool_returns_task_id()
    {
        var runner = new BackgroundTaskRunner();
        var host = new FakeSubagentHost("out");
        var context = MakeContext(runner, host);
        var tool = new BackgroundTaskStartTool();

        var input = Json("""{"prompt":"do something"}""");
        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("Started background task", result.Content);

        // Verify an id was returned that we can poll.
        var list = runner.List();
        Assert.Single(list);
    }

    [Fact]
    public async Task TaskStartTool_graceful_when_no_runner()
    {
        var context = new ToolContext(".")
        {
            Subagents = new FakeSubagentHost("out"),
        };
        var tool = new BackgroundTaskStartTool();

        var input = Json("""{"prompt":"do something"}""");
        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("not available", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TaskOutputTool_returns_output_and_status()
    {
        var runner = new BackgroundTaskRunner();
        var host = new FakeSubagentHost("the output text");
        var context = MakeContext(runner, host);

        var id = runner.Start(host, "general-purpose", "go");

        // Wait until completed.
        var attempts = 0;
        BackgroundTaskStatus status;
        do
        {
            await Task.Delay(10);
            var (_, s) = runner.Read(id);
            status = s;
            attempts++;
        }
        while (status == BackgroundTaskStatus.Running && attempts < 200);

        var tool = new BackgroundTaskOutputTool();
        var input = Json($$$"""{"task_id":"{{{id}}}"}""");
        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("[status:", result.Content);
    }

    [Fact]
    public async Task TaskOutputTool_unknown_id_reports_not_found()
    {
        var runner = new BackgroundTaskRunner();
        var host = new FakeSubagentHost("out");
        var context = MakeContext(runner, host);
        var tool = new BackgroundTaskOutputTool();

        var input = Json("""{"task_id":"nonexistent"}""");
        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("not found", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TaskStopTool_stops_a_running_task()
    {
        var runner = new BackgroundTaskRunner();
        var host = new CancellableFakeHost("partial");
        var context = MakeContext(runner, host);

        var id = runner.Start(host, "general-purpose", "go");

        // Wait until it starts producing output.
        var attempts = 0;
        string captured = string.Empty;
        while (captured.Length == 0 && attempts < 200)
        {
            await Task.Delay(10);
            var (text, _) = runner.Read(id);
            captured += text;
            attempts++;
        }

        var tool = new BackgroundTaskStopTool();
        var input = Json($$$"""{"task_id":"{{{id}}}"}""");
        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("stopped", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TaskStopTool_unknown_id_reports_not_found()
    {
        var runner = new BackgroundTaskRunner();
        var host = new FakeSubagentHost("out");
        var context = MakeContext(runner, host);
        var tool = new BackgroundTaskStopTool();

        var input = Json("""{"task_id":"ghost"}""");
        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("not found", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaskStartTool_is_readonly_matches_task_tool()
    {
        var taskStart = new BackgroundTaskStartTool();
        var taskTool = new TaskTool();

        Assert.Equal(taskTool.IsReadOnly, taskStart.IsReadOnly);
    }

    [Fact]
    public void BackgroundTaskTools_are_in_built_in_set()
    {
        var names = BuiltInTools.All().Select(t => t.Name).ToList();

        Assert.Contains("task_start", names);
        Assert.Contains("task_output", names);
        Assert.Contains("task_stop", names);
    }

    /// <summary>
    /// Scripted ILlmClient that replays pre-built event sequences, one per turn.
    /// </summary>
    private sealed class ScriptedClient(params IReadOnlyList<LlmClient.AssistantStreamEvent>[] turns) : LlmClient.ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<LlmClient.AssistantStreamEvent> StreamAsync(
            LlmClient.ChatRequest request,
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

    [Fact]
    public async Task AgentLoop_threads_background_task_runner_to_context()
    {
        // A real end-to-end AgentLoop test: the LLM calls task_start, which
        // requires context.BackgroundTasks and context.Subagents to be threaded
        // through from the injected runner and host. After the loop completes
        // the runner must contain one registered task.
        var toolTurn = new[]
        {
            LlmClient.AssistantStreamEvent.Tool(new LlmClient.ToolUseBlock(
                "t1",
                "task_start",
                """{"prompt":"background work"}""")),
            LlmClient.AssistantStreamEvent.Finished("tool_use"),
        };
        var endTurn = new[]
        {
            LlmClient.AssistantStreamEvent.Delta("done"),
            LlmClient.AssistantStreamEvent.Finished("end_turn"),
        };

        var runner = new BackgroundTaskRunner();
        var host = new FakeSubagentHost("subagent result");

        var loop = new AgentLoop(
            new ScriptedClient(toolTurn, endTurn),
            new ToolRegistry([new BackgroundTaskStartTool()]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" },
            subagents: host,
            backgroundTasks: runner);

        var history = new List<LlmClient.ChatMessage> { LlmClient.ChatMessage.UserText("start a task") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // AgentLoop must have threaded the runner into ToolContext so task_start
        // could register the task.
        var tasks = runner.List();
        Assert.Single(tasks);
    }
}
