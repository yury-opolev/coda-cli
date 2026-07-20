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
    /// unknown, or when a Subagent would exceed MaxSubagentDepth.
    /// </summary>
    internal ManagedTask Register(TaskKind kind, string description, string? parentTaskId)
    {
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

        lock (_gate)
        {
            var id = $"task-{++_nextId:D4}";
            var logPath = Path.Combine(LogRoot, SessionId, id + ".log");
            var task = new ManagedTask(
                id, parentTaskId, depth, kind, description, logPath, _outputRingBytes, OnTaskTerminal);
            // Publish to the dictionary and the order list atomically under the
            // same lock so id assignment, registration order, and lookup never
            // observe a task in one collection but not the other.
            _order.Add(task);
            _tasks[id] = task;
            // The writer constructor performs no I/O (it only stores the path), so it is
            // safe to create under the registry lock; disk I/O happens lazily on Append.
            _logs[id] = new TaskLogWriter(task.LogPath);
            return task;
        }
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

    /// <summary>Appends output to a task's ring and persistent log. No-op if the id is unknown.</summary>
    public void AppendOutput(string id, string text)
    {
        // Deliberately not under _gate: writing to the ring and the persistent log
        // (disk I/O) must never happen while the registry lock is held.
        if (Find(id) is not { } t) return;
        t.Append(text);
        if (_logs.TryGetValue(id, out var log))
        {
            log.Append(text);
        }
    }

    /// <summary>Reads incremental output for a task. Returns null if the id is unknown.</summary>
    public (string Text, long NextCursor, bool Truncated)? TryReadIncremental(string id, long cursor) =>
        Find(id) is { } t ? t.ReadIncremental(cursor) : null;

    /// <summary>Returns the output tail for a task, or null if the id is unknown.</summary>
    public string? TryPeek(string id, int maxChars) => Find(id)?.Peek(maxChars);

    public void Dispose()
    {
        // Snapshot the task set under the lock, then dispose outside it. Disposing a
        // ManagedTask cancels its token, which synchronously runs user cancellation
        // callbacks; holding _gate across those callbacks can deadlock against readers
        // (List/Get) that need the same lock. Log writers flush to disk on Dispose, so
        // they must also be closed outside the lock (no disk I/O under the registry lock).
        ManagedTask[] tasks;
        lock (_gate)
        {
            tasks = _order.ToArray();
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
