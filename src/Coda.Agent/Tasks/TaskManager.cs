using System.Collections.Concurrent;

namespace Coda.Agent.Tasks;

/// <summary>
/// In-process registry and coordinator for all long-running work in a session
/// (subagents and shells). Owns task identity, the depth model, and (in later
/// tasks) output fan-out, persistent logs, change subscriptions, and shutdown.
/// </summary>
public sealed partial class TaskManager : IDisposable
{
    /// <summary>Maximum subagent nesting depth. Main agent is depth 0; deepest subagent is depth 2.</summary>
    public const int MaxSubagentDepth = 2;

    private readonly object _gate = new();
    private readonly List<ManagedTask> _order = new();
    private readonly ConcurrentDictionary<string, ManagedTask> _tasks = new();
    private readonly ConcurrentDictionary<string, TaskLogWriter> _logs = new();
    private readonly List<TaskSubscription> _subs = new();
    private readonly long _outputRingBytes;
    private int _nextId;

    public TaskManager(
        string sessionId,
        string? logRoot = null,
        long outputRingBytes = OutputRing.DefaultMaxBytes)
    {
        if (outputRingBytes <= 0) throw new ArgumentOutOfRangeException(nameof(outputRingBytes));
        SessionId = sessionId;
        LogRoot = logRoot ?? DefaultLogRoot;
        _outputRingBytes = outputRingBytes;

        // Best-effort startup housekeeping; never blocks or throws into construction.
        try
        {
            TaskLogRetention.Cleanup(LogRoot, TaskLogRetention.MaxAge, TaskLogRetention.GlobalCapBytes);
        }
        catch
        {
            // ignore — logging is diagnostic, not load-bearing.
        }
    }

    public string SessionId { get; }

    /// <summary>Root directory for persistent task logs.</summary>
    public string LogRoot { get; }

    public static string DefaultLogRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".coda", "task-logs");

    /// <summary>
    /// Registers a new task and returns it in the Running state. Derives depth
    /// from the parent (null parent => depth 1). Throws when the parent id is
    /// unknown, or when a Subagent would exceed MaxSubagentDepth. The optional
    /// <paramref name="mode"/> records whether the task runs in the foreground or the
    /// background; it defaults to <see cref="TaskExecutionMode.Foreground"/> so existing call
    /// sites are unchanged.
    /// </summary>
    internal ManagedTask Register(
        TaskKind kind,
        string description,
        string? parentTaskId,
        TaskExecutionMode mode = TaskExecutionMode.Foreground)
    {
        // Once shutdown has begun the manager stops accepting registrations so no new subagent
        // loop or shell process can start during teardown and outlive it. Checked first, before
        // any id assignment, so a rejected start leaves the registry untouched.
        if (_shuttingDown)
        {
            throw new InvalidOperationException(
                "Task manager is shutting down; no new tasks may be registered.");
        }

        int depth;
        if (parentTaskId is null)
        {
            depth = 1;
        }
        else if (_tasks.TryGetValue(parentTaskId, out var parent))
        {
            depth = parent.Depth + 1;
        }
        else
        {
            throw new InvalidOperationException($"Unknown parent task '{parentTaskId}'.");
        }

        if (kind == TaskKind.Subagent && depth > MaxSubagentDepth)
        {
            throw new InvalidOperationException(
                $"Subagent nesting depth {depth} exceeds maximum {MaxSubagentDepth}.");
        }

        ManagedTask task;
        long createdVersion;
        TaskSubscription[] subs;
        lock (_gate)
        {
            var id = $"task-{++_nextId:D4}";
            // This runtime issues `task-NNNN` ids and is now the single owner of all subagent
            // and shell tasks; the legacy background-task runner (and its `bgNNNN` id space) has
            // been removed.
            var logPath = Path.Combine(LogRoot, SessionId, id + ".log");
            task = new ManagedTask(
                id, parentTaskId, depth, kind, description, logPath, _outputRingBytes, mode, OnTaskTerminal);
            // Publish to the dictionary and the order list atomically under the
            // same lock so id assignment, registration order, and lookup never
            // observe a task in one collection but not the other.
            _order.Add(task);
            _tasks[id] = task;
            // The writer constructor performs no I/O (it only stores the path), so it is
            // safe to create under the registry lock; disk I/O happens lazily on Append.
            _logs[id] = new TaskLogWriter(task.LogPath);
            createdVersion = task.Version;
            // Capture the subscriber list in the SAME critical section that publishes the
            // task. This makes Subscribe and Register race-consistent: a concurrent
            // subscriber either takes its snapshot before this lock (so it is in _subs here
            // and receives the Created change) or after (so the task is already in its
            // initial snapshot and it is absent from this captured list) — exactly one path,
            // never both, never neither.
            subs = _subs.Count == 0 ? Array.Empty<TaskSubscription>() : _subs.ToArray();
        }

        // Post outside the registry lock so a slow/blocking subscriber cannot stall
        // registration or invert lock ordering against the subscription's own gate.
        if (subs.Length > 0)
        {
            var change = new TaskChange(task.Id, createdVersion, TaskChangeKind.Created);
            foreach (var sub in subs)
            {
                sub.Post(change);
            }
        }

        return task;
    }

    /// <summary>
    /// Terminal-state hook invoked by <see cref="ManagedTask"/> outside its own lock. Closes and
    /// removes the task's log writer, flushing any buffered final output. Runs without the
    /// registry lock (ConcurrentDictionary), so it cannot deadlock against readers, and it never
    /// performs disk I/O under <see cref="_gate"/>.
    /// </summary>
    private void OnTaskTerminal(ManagedTask task)
    {
        if (_logs.TryRemove(task.Id, out var log))
        {
            log.Dispose();
        }
    }

    /// <summary>Test seam: true while a live log writer is registered for the task id.</summary>
    internal bool HasLogWriter(string id) => _logs.ContainsKey(id);

    /// <summary>Returns the snapshot for a task, or null if the id is unknown.</summary>
    public TaskSnapshot? Get(string id) =>
        _tasks.TryGetValue(id, out var t) ? t.ToSnapshot() : null;

    /// <summary>Returns snapshots for all tasks in registration order.</summary>
    public IReadOnlyList<TaskSnapshot> List()
    {
        lock (_gate)
        {
            return _order.Select(t => t.ToSnapshot()).ToList();
        }
    }

    /// <summary>Returns the live task for an id, or null. Internal for tools/host use.</summary>
    internal ManagedTask? Find(string id) =>
        _tasks.TryGetValue(id, out var t) ? t : null;

    /// <summary>
    /// Removes a terminal task from the manager. Returns <see cref="TaskActionResult.Rejected"/>
    /// while it is still running, <see cref="TaskActionResult.NotFound"/> for unknown ids, and
    /// <see cref="TaskActionResult.Ok"/> once removed: the task is dropped from the registry and
    /// order list, its per-consumer cursors/steering/process refs are released via
    /// <see cref="ManagedTask.Dispose"/>, its log writer (if any) is disposed (flushing/closing
    /// it), and a <see cref="TaskChangeKind.Removed"/> change is published at the task's final
    /// version so subscribers observe the removal.
    /// </summary>
    public TaskActionResult Remove(string id)
    {
        ManagedTask task;
        lock (_gate)
        {
            var index = _order.FindIndex(t => t.Id == id);
            if (index < 0)
            {
                return TaskActionResult.NotFound;
            }

            task = _order[index];
            if (task.Status == TaskRunStatus.Running)
            {
                // Only terminal tasks may be removed; a running task must be stopped first.
                return TaskActionResult.Rejected;
            }

            _order.RemoveAt(index);
        }

        _tasks.TryRemove(id, out _);
        if (_logs.TryRemove(id, out var log))
        {
            log.Dispose();
        }

        // Publish the task's final version with the Removed kind. The task is already terminal,
        // so its version is stable and no further change can follow it.
        var version = task.Version;
        task.Dispose();
        Publish(id, version, TaskChangeKind.Removed);
        return TaskActionResult.Ok;
    }

    /// <summary>
    /// Appends output to a task's ring and persistent log, then publishes an Output change
    /// carrying the EXACT version the append assigned. A no-op — no version bump, no log
    /// write, no notification, no waiter wake — when the id is unknown, the text is
    /// empty/null, or the task is already terminal.
    /// </summary>
    public void AppendOutput(string id, string text)
    {
        // Empty/null input is a complete no-op: short-circuit before touching the task,
        // the log, or any subscriber.
        if (string.IsNullOrEmpty(text)) return;

        // Deliberately not under _gate: writing to the ring and the persistent log
        // (disk I/O) must never happen while the registry lock is held.
        if (Find(id) is not { } t) return;
        if (t.TryAppend(text) is not { } version) return; // terminal or no-op append

        if (_logs.TryGetValue(id, out var log))
        {
            log.Append(text);
        }

        // Publish the exact assigned version (never a re-read of the live version) so
        // subscribers can validate contiguity. The ring/log append already happened, so a
        // woken subscriber that reads output observes the just-appended text.
        Publish(id, version, TaskChangeKind.Output);
    }

    /// <summary>Reads incremental output for a task. Returns null if the id is unknown.</summary>
    public (string Text, long NextCursor, bool Truncated)? TryReadIncremental(string id, long cursor) =>
        Find(id) is { } t ? t.ReadIncremental(cursor) : null;

    /// <summary>Returns the output tail for a task, or null if the id is unknown.</summary>
    public string? TryPeek(string id, int maxChars) => Find(id)?.Peek(maxChars);

    /// <summary>Creates a subscription seeded with the current task list.</summary>
    public TaskSubscription Subscribe(int capacity = TaskSubscription.DefaultCapacity)
    {
        lock (_gate)
        {
            // List() and _subs.Add run in one critical section so the initial snapshot and
            // the subscriber's registration are consistent with concurrent Register calls.
            var sub = new TaskSubscription(List(), capacity, Unsubscribe);
            _subs.Add(sub);
            return sub;
        }
    }

    /// <summary>
    /// Closes and detaches a subscription. Internal callback wired into each subscription's
    /// <see cref="TaskSubscription.Dispose"/> so the only public teardown path is
    /// <c>Dispose</c>, which both stops delivery and wakes waiters — there is no public
    /// "unsubscribe but keep hanging" footgun.
    /// </summary>
    private void Unsubscribe(TaskSubscription subscription)
    {
        lock (_gate)
        {
            _subs.Remove(subscription);
        }
    }

    /// <summary>Transitions a task to Completed and publishes a status change. Returns false if already terminal or unknown.</summary>
    public bool Complete(string id, string? result)
    {
        if (Find(id) is not { } t || !t.TryComplete(result, out var version)) return false;
        Publish(id, version, TaskChangeKind.Status);
        return true;
    }

    /// <summary>Transitions a task to Failed and publishes a status change. Returns false if already terminal or unknown.</summary>
    public bool Fail(string id, string? error)
    {
        if (Find(id) is not { } t || !t.TryFail(error, out var version)) return false;
        Publish(id, version, TaskChangeKind.Status);
        return true;
    }

    /// <summary>Transitions a task to Stopped and publishes a status change. Returns false if already terminal or unknown.</summary>
    public bool Stop(string id)
    {
        if (Find(id) is not { } t || !t.TryStop(out var version)) return false;
        Publish(id, version, TaskChangeKind.Status);
        return true;
    }

    /// <summary>
    /// Fans a change out to every current subscriber. The subscriber list is snapshotted
    /// under the registry lock, but <see cref="TaskSubscription.Post"/> is invoked OUTSIDE
    /// the lock so a slow subscriber cannot stall producers or invert lock ordering.
    /// </summary>
    private void Publish(string taskId, long version, TaskChangeKind kind)
    {
        TaskSubscription[] subs;
        lock (_gate)
        {
            if (_subs.Count == 0) return;
            subs = _subs.ToArray();
        }

        var change = new TaskChange(taskId, version, kind);
        foreach (var sub in subs)
        {
            sub.Post(change);
        }
    }

    public void Dispose()
    {
        // Snapshot the task set under the lock, then dispose outside it. Disposing a
        // ManagedTask cancels its token, which synchronously runs user cancellation
        // callbacks; holding _gate across those callbacks can deadlock against readers
        // (List/Get) that need the same lock. Log writers flush to disk on Dispose, so
        // they must also be closed outside the lock (no disk I/O under the registry lock).
        ManagedTask[] tasks;
        TaskSubscription[] subs;
        lock (_gate)
        {
            // Idempotent: a second Dispose (e.g. after ShutdownAsync already disposed) is a no-op.
            if (_disposed) return;
            _disposed = true;

            tasks = _order.ToArray();
            // Snapshot AND clear subscriptions under the lock so no late publish reaches a
            // subscription after this point, then close them outside the lock (below).
            subs = _subs.ToArray();
            _subs.Clear();
        }

        // Close subscriptions outside the lock: Close() takes each subscription's own gate
        // and wakes any pending waiter so blocked consumers can observe IsClosed and exit.
        foreach (var sub in subs)
        {
            sub.Close();
        }

        foreach (var t in tasks)
        {
            t.Dispose();
        }

        foreach (var log in _logs.Values)
        {
            log.Dispose();
        }
    }
}
