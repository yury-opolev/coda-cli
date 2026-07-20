namespace Coda.Agent.Tasks;

public sealed partial class TaskManager
{
    /// <summary>
    /// Registers a foreground subagent task, runs it to completion via <paramref name="host"/>,
    /// streams its output into the task ring/log, and returns its final report. Foreground means
    /// the caller awaits the result; the task is registered exactly like a background one.
    /// </summary>
    public async Task<string> RunSubagentForegroundAsync(
        ISubagentHost host,
        string subagentType,
        string prompt,
        string description,
        IAgentSink parentSink,
        string? parentTaskId,
        CancellationToken cancellationToken = default)
    {
        var task = Register(TaskKind.Subagent, description, parentTaskId);
        var steering = new SteeringInbox();
        task.AttachSteering(steering);

        var sink = new TaskOutputSink(this, task.Id, parentSink);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, task.Token);
        try
        {
            var result = await host
                .RunSubagentAsync(subagentType, prompt, sink, steering, task.Id, task.Depth, linked.Token)
                .ConfigureAwait(false);
            Complete(task.Id, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            Stop(task.Id);
            return "(subagent stopped)";
        }
        catch (Exception ex)
        {
            Fail(task.Id, ex.Message);
            return $"(subagent failed: {ex.Message})";
        }
    }

    /// <summary>
    /// Registers a background subagent task, starts it on the thread pool, and returns its id
    /// immediately. Progress is polled via <c>task_output</c> and cancelled via <c>task_stop</c>.
    /// </summary>
    public string StartSubagentBackground(
        ISubagentHost host,
        string subagentType,
        string prompt,
        string description,
        string? parentTaskId)
    {
        var task = Register(TaskKind.Subagent, description, parentTaskId);
        var steering = new SteeringInbox();
        task.AttachSteering(steering);
        var sink = new TaskOutputSink(this, task.Id, NullAgentSink.Instance);

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await host
                    .RunSubagentAsync(subagentType, prompt, sink, steering, task.Id, task.Depth, task.Token)
                    .ConfigureAwait(false);
                Complete(task.Id, result);
            }
            catch (OperationCanceledException)
            {
                Stop(task.Id);
            }
            catch (Exception ex)
            {
                Fail(task.Id, ex.Message);
            }
        });

        return task.Id;
    }

    /// <summary>Requests cancellation of a running task (backs <c>task_stop</c>).</summary>
    public TaskActionResult RequestStop(string id)
    {
        var t = Find(id);
        if (t is null) return TaskActionResult.NotFound;
        if (t.Status != TaskRunStatus.Running) return TaskActionResult.InvalidState;
        t.Cancel();
        return TaskActionResult.Ok;
    }

    /// <summary>Queues a steering message for a running subagent (backs <c>task_send</c>).</summary>
    public TaskActionResult Steer(string id, string message)
    {
        var t = Find(id);
        if (t is null) return TaskActionResult.NotFound;
        if (t.Kind != TaskKind.Subagent) return TaskActionResult.Rejected;
        if (t.Status != TaskRunStatus.Running) return TaskActionResult.InvalidState;
        if (t.Steering is null) return TaskActionResult.Rejected;
        t.Steering.Enqueue(message);
        return TaskActionResult.Ok;
    }

    /// <summary>
    /// Reads incremental output for the main agent's server-side cursor (backs <c>task_output</c>).
    /// Returns Found=false when the id is unknown.
    /// </summary>
    public (bool Found, string Text, bool Truncated, TaskRunStatus Status) ReadForMainAgent(string id)
    {
        if (Find(id) is not { } t)
        {
            return (false, string.Empty, false, TaskRunStatus.Running);
        }

        var (text, truncated, status) = t.ReadFromMainCursor();
        return (true, text, truncated, status);
    }

    /// <summary>
    /// A sink that appends a subagent's assistant text and tool activity to the task's
    /// ring/log while forwarding every event to the parent sink (real for foreground,
    /// <see cref="NullAgentSink"/> for background).
    /// </summary>
    private sealed class TaskOutputSink : IAgentSink
    {
        private readonly TaskManager _manager;
        private readonly string _taskId;
        private readonly IAgentSink _parent;

        public TaskOutputSink(TaskManager manager, string taskId, IAgentSink parent)
        {
            _manager = manager;
            _taskId = taskId;
            _parent = parent;
        }

        public void OnAssistantText(string delta)
        {
            _manager.AppendOutput(_taskId, delta);
            _parent.OnAssistantText(delta);
        }

        public void OnAssistantTextComplete() => _parent.OnAssistantTextComplete();

        public void OnToolCall(string toolName, string inputPreview)
        {
            _manager.AppendOutput(_taskId, $"\n[tool: {toolName}]\n");
            _parent.OnToolCall(toolName, inputPreview);
        }

        public void OnToolResult(string toolName, ToolResult result)
        {
            _manager.AppendOutput(_taskId, $"[/{toolName}]\n");
            _parent.OnToolResult(toolName, result);
        }

        public void OnError(string message)
        {
            _manager.AppendOutput(_taskId, $"[error: {message}]\n");
            _parent.OnError(message);
        }
    }

    /// <summary>An IAgentSink that discards everything — used as the parent for background subagents.</summary>
    private sealed class NullAgentSink : IAgentSink
    {
        public static readonly NullAgentSink Instance = new();

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }
}
