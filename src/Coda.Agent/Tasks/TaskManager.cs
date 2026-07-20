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
    private int _nextId;

    public TaskManager(string sessionId, string? logRoot = null)
    {
        SessionId = sessionId;
        LogRoot = logRoot ?? DefaultLogRoot;
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
            var task = new ManagedTask(id, parentTaskId, depth, kind, description, logPath);
            // Publish to the dictionary and the order list atomically under the
            // same lock so id assignment, registration order, and lookup never
            // observe a task in one collection but not the other.
            _order.Add(task);
            _tasks[id] = task;
            return task;
        }
    }

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

    public void Dispose()
    {
        // Snapshot the task set under the lock, then dispose outside it. Disposing a
        // ManagedTask cancels its token, which synchronously runs user cancellation
        // callbacks; holding _gate across those callbacks can deadlock against readers
        // (List/Get) that need the same lock.
        ManagedTask[] tasks;
        lock (_gate)
        {
            tasks = _order.ToArray();
        }

        foreach (var t in tasks)
        {
            t.Dispose();
        }
    }
}
