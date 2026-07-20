using LlmClient;

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
        var task = Register(TaskKind.Subagent, description, parentTaskId, TaskExecutionMode.Foreground);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's own token (turn/caller cancellation) fired: this task is being torn
            // down as part of unwinding the enclosing turn. Mark it Stopped, then rethrow so the
            // parent AgentLoop unwinds immediately instead of swallowing the cancellation.
            Stop(task.Id);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Only the task-scoped token fired (task_stop / RequestStop): this is a graceful,
            // self-contained stop. Report it via the legacy compatibility result so the caller
            // keeps running rather than unwinding.
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
        var task = Register(TaskKind.Subagent, description, parentTaskId, TaskExecutionMode.Background);
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

    /// <summary>
    /// True when <paramref name="callerTaskId"/> is authorized to read or stop
    /// <paramref name="targetId"/>. The main agent (null caller) has authority over every task in
    /// the session. A subagent has authority only over its own descendants: the caller must be a
    /// <em>strict</em> ancestor of the target, walking the target's parent chain from the trusted
    /// registry graph — never from caller-supplied depth. Consequently a task can never act on
    /// itself, its parent, a sibling, or an unrelated task, and unknown targets are unauthorized.
    /// </summary>
    internal bool IsAuthorizedCaller(string targetId, string? callerTaskId)
    {
        // Main agent: full authority over the whole session's task set.
        if (callerTaskId is null) return true;

        var target = Find(targetId);
        if (target is null) return false;

        // Walk strict ancestors of the target. The parent graph is a tree rooted at the main
        // agent, so this terminates; the counter is a defensive cap against a corrupt graph.
        var parentId = target.ParentId;
        var guard = 0;
        while (parentId is not null)
        {
            if (parentId == callerTaskId) return true;
            if (++guard > 64) return false;
            if (Find(parentId) is not { } parent) return false;
            parentId = parent.ParentId;
        }

        return false;
    }

    /// <summary>
    /// Lists the tasks visible to <paramref name="callerTaskId"/>: the main agent (null caller)
    /// sees every task in the session; a subagent sees only its <em>strict descendants</em> (its
    /// own subtree). A subagent never sees itself, its ancestors, siblings, or unrelated branches,
    /// so the snapshot leaks no ids outside its subtree.
    /// </summary>
    public IReadOnlyList<TaskSnapshot> List(string? callerTaskId)
    {
        if (callerTaskId is null) return List();

        lock (_gate)
        {
            // IsAuthorizedCaller reads the parent graph via Find (ConcurrentDictionary), never the
            // registry lock, so evaluating it under _gate introduces no re-entrancy or inversion.
            return _order
                .Where(t => IsAuthorizedCaller(t.Id, callerTaskId))
                .Select(t => t.ToSnapshot())
                .ToList();
        }
    }

    /// <summary>
    /// Returns the snapshot for <paramref name="id"/> only when <paramref name="callerTaskId"/> is
    /// authorized for it. An unauthorized target and an unknown id both return null, so a subagent
    /// cannot distinguish a task it does not own from one that does not exist.
    /// </summary>
    public TaskSnapshot? Get(string id, string? callerTaskId) =>
        IsAuthorizedCaller(id, callerTaskId) ? Get(id) : null;

    /// <summary>
    /// Peeks the output tail for <paramref name="id"/> on behalf of <paramref name="callerTaskId"/>
    /// without advancing any cursor. Returns null when the id is unknown OR the caller is
    /// unauthorized (indistinguishable), and the peeked text otherwise (empty when no output yet).
    /// </summary>
    public string? TryPeek(string id, int maxChars, string? callerTaskId) =>
        IsAuthorizedCaller(id, callerTaskId) ? TryPeek(id, maxChars) : null;

    /// <summary>
    /// Requests cancellation of a running task on behalf of <paramref name="callerTaskId"/>
    /// (backs <c>task_stop</c>). Returns <see cref="TaskActionResult.Denied"/> when the caller is
    /// not authorized for the target, checked BEFORE any state inspection so an unauthorized
    /// caller cannot distinguish a running task from a finished one.
    /// </summary>
    public TaskActionResult RequestStop(string id, string? callerTaskId)
    {
        var t = Find(id);
        if (t is null) return TaskActionResult.NotFound;
        if (!IsAuthorizedCaller(id, callerTaskId)) return TaskActionResult.Denied;
        if (t.Status != TaskRunStatus.Running) return TaskActionResult.InvalidState;
        t.Cancel();
        return TaskActionResult.Ok;
    }

    /// <summary>Compatibility overload for the main agent (full authority over every task).</summary>
    public TaskActionResult RequestStop(string id) => RequestStop(id, callerTaskId: null);

    /// <summary>
    /// Queues a steering message for a running subagent on behalf of <paramref name="callerTaskId"/>
    /// (backs <c>task_send</c>). The main agent (null caller) may steer any task; a subagent may
    /// steer only its own running descendants. Authorization is checked BEFORE any state
    /// inspection — right after the existence check — so an unauthorized caller (mapped to the
    /// same wording as NotFound by the tool) cannot distinguish a shell from a subagent, a running
    /// task from a terminal one, or probe the existence of tasks outside its subtree. Returns
    /// <see cref="TaskActionResult.Rejected"/> for a shell or a subagent with no steering inbox,
    /// and <see cref="TaskActionResult.InvalidState"/> for a terminal subagent.
    /// </summary>
    public TaskActionResult Steer(string id, string message, string? callerTaskId)
    {
        var t = Find(id);
        if (t is null) return TaskActionResult.NotFound;
        if (!IsAuthorizedCaller(id, callerTaskId)) return TaskActionResult.Denied;
        if (t.Kind != TaskKind.Subagent) return TaskActionResult.Rejected;
        if (t.Status != TaskRunStatus.Running) return TaskActionResult.InvalidState;
        if (t.Steering is null) return TaskActionResult.Rejected;
        t.Steering.Enqueue(message);
        return TaskActionResult.Ok;
    }

    /// <summary>Compatibility overload for the main agent (full authority over every task).</summary>
    public TaskActionResult Steer(string id, string message) => Steer(id, message, callerTaskId: null);

    /// <summary>
    /// Reads incremental output for a caller's own server-side cursor (backs <c>task_output</c>).
    /// The main agent (null caller) may read any task; a subagent may read only its own
    /// descendants. Returns Found=false when the id is unknown OR the caller is unauthorized —
    /// the two are indistinguishable so a subagent cannot probe the existence of tasks it does
    /// not own. Each consumer (main sentinel or caller task id) advances an independent cursor,
    /// so consumers never steal one another's incremental output.
    /// </summary>
    public (bool Found, string Text, bool Truncated, TaskRunStatus Status) ReadOutput(string id, string? callerTaskId)
    {
        var t = Find(id);
        if (t is null || !IsAuthorizedCaller(id, callerTaskId))
        {
            return (false, string.Empty, false, TaskRunStatus.Running);
        }

        var consumerId = callerTaskId ?? ManagedTask.MainConsumerId;
        var (text, truncated, status) = t.ReadFromCursor(consumerId);
        return (true, text, truncated, status);
    }

    /// <summary>
    /// Compatibility overload: reads incremental output on the main agent's cursor (full
    /// authority). Returns Found=false when the id is unknown.
    /// </summary>
    public (bool Found, string Text, bool Truncated, TaskRunStatus Status) ReadForMainAgent(string id) =>
        ReadOutput(id, callerTaskId: null);

    /// <summary>
    /// A sink that appends a subagent's assistant text and tool activity to the task's
    /// ring/log while forwarding every event to the parent sink (real for foreground,
    /// <see cref="NullAgentSink"/> for background). Forwarding is total: every
    /// <see cref="IAgentSink"/> event — including the optional
    /// <see cref="IAgentSink.OnToolProgress"/>, <see cref="IAgentSink.OnLimitReached"/>,
    /// <see cref="IAgentSink.OnStopReason"/>, and <see cref="IAgentSink.OnUsage"/> pulses —
    /// reaches the parent exactly once. High-frequency progress/usage pulses are deliberately
    /// kept out of the ring/log to avoid drowning the readable transcript, but concise
    /// limit/stop/error markers are appended so those milestones stay visible in task_output.
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

        // A liveness pulse: forwarded so an orchestrator sees the subagent is alive, but kept
        // out of the ring/log because it fires repeatedly and would flood the transcript.
        public void OnToolProgress(string toolName, long elapsedMs) =>
            _parent.OnToolProgress(toolName, elapsedMs);

        public void OnError(string message)
        {
            _manager.AppendOutput(_taskId, $"[error: {message}]\n");
            _parent.OnError(message);
        }

        // A recoverable per-turn limit — a meaningful milestone, so it earns a ring marker.
        public void OnLimitReached(string kind, string message)
        {
            _manager.AppendOutput(_taskId, $"[limit: {kind}: {message}]\n");
            _parent.OnLimitReached(kind, message);
        }

        // The turn's stop reason — appended as a concise marker so it stays visible.
        public void OnStopReason(string? stopReason)
        {
            if (!string.IsNullOrEmpty(stopReason))
            {
                _manager.AppendOutput(_taskId, $"[stop: {stopReason}]\n");
            }

            _parent.OnStopReason(stopReason);
        }

        // Token usage: forwarded for accounting, but kept out of the ring/log as it is
        // per-iteration accounting noise rather than readable transcript content.
        public void OnUsage(TokenUsage usage) => _parent.OnUsage(usage);
    }

    /// <summary>An IAgentSink that discards everything — used as the parent for background subagents.</summary>
    private sealed class NullAgentSink : IAgentSink
    {
        public static readonly NullAgentSink Instance = new();

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnToolProgress(string toolName, long elapsedMs) { }
        public void OnError(string message) { }
        public void OnLimitReached(string kind, string message) { }
        public void OnStopReason(string? stopReason) { }
        public void OnUsage(TokenUsage usage) { }
    }
}
