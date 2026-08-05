using System.Collections.Concurrent;

namespace Coda.Agent.Tasks;

/// <summary>
/// A terminal-state notification for a background task, delivered via the per-owner
/// completion outbox on <see cref="TaskManager"/>.
/// </summary>
/// <param name="TaskId">The task identifier that reached a terminal state.</param>
/// <param name="Description">Human-readable label of the task (from <see cref="ManagedTask.Description"/>).</param>
/// <param name="Status">The terminal status: <see cref="TaskRunStatus.Completed"/>, <see cref="TaskRunStatus.Failed"/>, or <see cref="TaskRunStatus.Stopped"/>.</param>
/// <param name="Report">The final result or error text, or <see langword="null"/> when not applicable.</param>
public sealed record TaskCompletionEntry(string TaskId, string Description, TaskRunStatus Status, string? Report);

/// <summary>
/// In-process registry and coordinator for all long-running work in a session
/// (subagents and shells). Owns task identity, the depth model, and (in later
/// tasks) output fan-out, persistent logs, change subscriptions, and shutdown.
/// </summary>
public sealed partial class TaskManager : IDisposable
{
    /// <summary>
    /// The nesting depth in force when nothing configures one. Main agent is depth 0, so 2 permits a
    /// subagent and a grandchild. Kept as the default rather than the law: <see cref="MaxSubagentDepth"/>
    /// is what callers should read, since a session may raise it.
    /// </summary>
    public const int DefaultMaxSubagentDepth = 2;

    /// <summary>The subagent limits and system-prompt policy this session is running under.</summary>
    /// <remarks>
    /// Exposed so collaborators that already hold the manager — the subagent host, the agent hook
    /// handler — read the session's settings from one place instead of being handed a second copy
    /// that could disagree with the depth and fan-out this manager is actually enforcing.
    /// </remarks>
    public Coda.Agent.Settings.SubagentSettings SubagentSettings { get; }

    /// <summary>Maximum subagent nesting depth for this session.</summary>
    public int MaxSubagentDepth { get; }

    /// <summary>How many subagent tasks may run at once in this session.</summary>
    public int MaxConcurrentSubagents { get; }

    /// <summary>
    /// Default upper bound on retained <em>terminal</em> tasks. Once more terminal tasks than this
    /// accumulate, the oldest are auto-pruned (running tasks are never pruned) so the registry,
    /// runtime snapshots, and <see cref="List()"/> stay bounded over a long session.
    /// </summary>
    public const int DefaultMaxRetainedTerminalTasks = 256;

    /// <summary>
    /// Maximum number of completion entries retained per owner in the outbox.
    /// When exceeded the oldest entry is silently dropped so the outbox cannot grow without
    /// bound against a stalled or dead consumer.
    /// </summary>
    public const int CompletionOutboxCapacity = 64;

    private readonly object _gate = new();
    private readonly List<ManagedTask> _order = new();
    private readonly ConcurrentDictionary<string, ManagedTask> _tasks = new();
    private readonly ConcurrentDictionary<string, TaskLogWriter> _logs = new();
    private readonly List<TaskSubscription> _subs = new();
    private readonly long _outputRingBytes;
    private readonly int _maxRetainedTerminalTasks;

    // Never disposed, deliberately. A background subagent returns its slot after the manager has
    // shut down, and disposing the semaphore would turn that release into an ObjectDisposedException
    // on a pool thread. SemaphoreSlim holds no unmanaged resource unless AvailableWaitHandle is
    // touched, which nothing here does.
    private readonly SemaphoreSlim _subagentSlots;    private int _nextId;

    // Per-owner completion outbox.  Key is the owner's task id, or MainAgentOutboxKey for the
    // main agent.  Guarded by _outboxGate, which is NEVER taken while _gate is held — the
    // enqueue site runs after Publish (outside _gate) to preserve the existing lock-ordering
    // invariant.
    private readonly object _outboxGate = new();
    private readonly Dictionary<string, Queue<TaskCompletionEntry>> _outbox = new(StringComparer.Ordinal);

    // Stable sentinel for the main-agent's outbox slot.  The leading NUL cannot collide with a
    // real task-NNNN id, so a subagent cannot masquerade as the main-agent consumer.
    private const string MainAgentOutboxKey = "\u0000main";

    /// <summary>Returns the dictionary key for <paramref name="ownerTaskId"/>, mapping null (main agent) to the stable sentinel.</summary>
    private static string OutboxKey(string? ownerTaskId) => ownerTaskId ?? MainAgentOutboxKey;

    private bool _idleLeaseHeld;

    /// <summary>
    /// Test-only barrier invoked inside <see cref="Register"/> after its pre-lock validation but
    /// BEFORE the registry lock is taken. Lets a test deterministically interleave a concurrent
    /// shutdown between a registration's checks and its under-lock commit to prove the atomicity
    /// of the shutdown/registration recheck. Null (and therefore free) in production.
    /// </summary>
    internal Action? RegisterBarrier { get; set; }

    /// <summary>
    /// Test-only notification raised under <see cref="_gate"/> immediately before a registration waits
    /// for an idle lease to release. Null in production.
    /// </summary>
    internal Action? IdleLeaseWaitBarrier { get; set; }

    public TaskManager(
        string sessionId,
        string? logRoot = null,
        long outputRingBytes = OutputRing.DefaultMaxBytes,
        int maxRetainedTerminalTasks = DefaultMaxRetainedTerminalTasks,
        Coda.Agent.Settings.SubagentSettings? subagentSettings = null)
    {
        if (outputRingBytes <= 0) throw new ArgumentOutOfRangeException(nameof(outputRingBytes));
        if (maxRetainedTerminalTasks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetainedTerminalTasks));
        }

        var limits = subagentSettings ?? Coda.Agent.Settings.SubagentSettings.Default;
        SubagentSettings = limits;
        MaxSubagentDepth = limits.MaxDepth;
        MaxConcurrentSubagents = limits.MaxConcurrent;
        _subagentSlots = new SemaphoreSlim(limits.MaxConcurrent, limits.MaxConcurrent);

        SessionId = sessionId;
        LogRoot = logRoot ?? DefaultLogRoot;
        _outputRingBytes = outputRingBytes;
        _maxRetainedTerminalTasks = maxRetainedTerminalTasks;

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

    /// <summary>
    /// Raised after a transition changes whether <see cref="TryAcquireIdleLease"/> can succeed. The
    /// callback always runs outside the manager's registry lock.
    /// </summary>
    public event Action? IdleStateChanged;

    /// <summary>Whether an idle lease can currently be acquired.</summary>
    public bool IsIdle
    {
        get
        {
            lock (_gate)
            {
                return IsIdleLocked();
            }
        }
    }

    /// <summary>
    /// Optional callback fired when a background task completes. Set by the session layer to
    /// fire <c>Notification(task-complete)</c> hooks. The callback receives
    /// <c>(kind, taskId)</c> and is invoked fire-and-forget; its returned task is discarded.
    /// </summary>
    public Func<string, string?, Task>? NotificationCallback { get; set; }

    public static string DefaultLogRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".coda", "task-logs");

    /// <summary>Subagent slots currently free; for diagnostics and tests.</summary>
    public int AvailableSubagentSlots => this._subagentSlots.CurrentCount;

    /// <summary>
    /// Takes one of the session's concurrent-subagent slots, or reports that none is free.
    /// </summary>
    /// <remarks>
    /// Non-blocking on purpose. Waiting here would hold the parent's turn open with nothing on screen
    /// to explain the stall, and the model could neither retry nor choose differently; refusing lets it
    /// see the limit and decide instead. The semaphore is never taken while <c>_gate</c> is held, so it
    /// cannot deadlock with registration or shutdown.
    /// </remarks>
    public bool TryAcquireSubagentSlot() => this._subagentSlots.Wait(0);

    /// <summary>Returns a slot taken by <see cref="TryAcquireSubagentSlot"/>. Safe to over-release.</summary>
    public void ReleaseSubagentSlot()
    {
        try
        {
            this._subagentSlots.Release();
        }
        catch (SemaphoreFullException)
        {
            // A double release is a caller bug, not a reason to fail a turn.
        }
    }

    /// <summary>
    /// Registers a new task and returns it in the Running state. Derives depth
    /// from the parent (null parent => depth 1). Throws when the parent id is
    /// unknown, when a Subagent would exceed MaxSubagentDepth, or when the manager is shutting
    /// down/disposed. The optional
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
        // Fast pre-lock rejection once shutdown has begun: skips depth work in the common
        // already-shutdown case. NOT authoritative on its own — the authoritative check runs under
        // _gate below, immediately before id/task creation, to close the register-vs-shutdown race.
        if (_shuttingDown || _disposed)
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

        if (kind == TaskKind.Subagent && depth > this.MaxSubagentDepth)
        {
            throw new InvalidOperationException(
                $"Subagent nesting depth {depth} exceeds maximum {this.MaxSubagentDepth}.");
        }

        // Test seam: deterministically interleave a concurrent shutdown here, after the pre-lock
        // checks but before the registry lock, to exercise the under-lock recheck below.
        RegisterBarrier?.Invoke();

        ManagedTask task;
        long createdVersion;
        TaskSubscription[] subs;
        bool becameBusy;
        lock (_gate)
        {
            while (_idleLeaseHeld && !_shuttingDown && !_disposed)
            {
                IdleLeaseWaitBarrier?.Invoke();
                Monitor.Wait(_gate);
            }

            // Authoritative recheck under the SAME lock that ShutdownAsync uses to set
            // _shuttingDown and snapshot the task set. This closes the race where a registration
            // passed the pre-lock check and then committed a task after shutdown had already
            // snapshotted/disposed — leaving a task that shutdown never cancelled or, worse, a
            // worker/log starting after disposal. If shutdown won the lock first, we throw here and
            // no id, task, or log writer is ever created.
            if (_shuttingDown || _disposed)
            {
                throw new InvalidOperationException(
                    "Task manager is shutting down; no new tasks may be registered.");
            }

            becameBusy = IsIdleLocked();
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

        if (becameBusy)
        {
            RaiseIdleStateChanged();
        }

        return task;
    }

    /// <summary>
    /// Atomically reserves a task-free interval. New registrations wait until the returned lease is
    /// released, and acquisition fails when any managed task is running or shutdown has begun.
    /// </summary>
    public IDisposable? TryAcquireIdleLease()
    {
        IDisposable? lease;
        lock (_gate)
        {
            if (!IsIdleLocked())
            {
                return null;
            }

            _idleLeaseHeld = true;
            lease = new IdleLease(this);
        }

        RaiseIdleStateChanged();
        return lease;
    }

    private void ReleaseIdleLease()
    {
        bool becameIdle;
        lock (_gate)
        {
            if (!_idleLeaseHeld)
            {
                return;
            }

            _idleLeaseHeld = false;
            Monitor.PulseAll(_gate);
            becameIdle = IsIdleLocked();
        }

        if (becameIdle)
        {
            RaiseIdleStateChanged();
        }
    }

    private sealed class IdleLease(TaskManager owner) : IDisposable
    {
        private TaskManager? owner = owner;

        public void Dispose() => Interlocked.Exchange(ref this.owner, null)?.ReleaseIdleLease();
    }

    /// <summary>
    /// Terminal-state hook invoked by <see cref="ManagedTask"/> outside its own lock. Closes and
    /// removes the task's log writer, flushing any buffered final output, then prunes the oldest
    /// terminal tasks back to <see cref="_maxRetainedTerminalTasks"/>. Runs without the registry
    /// lock held on entry (ConcurrentDictionary), so it cannot deadlock against readers, and it
    /// never performs disk I/O under <see cref="_gate"/>.
    /// </summary>
    private void OnTaskTerminal(ManagedTask task)
    {
        if (_logs.TryRemove(task.Id, out var log))
        {
            log.Dispose();
        }

        PruneTerminalTasks();
    }

    /// <summary>
    /// Auto-prunes the oldest <em>terminal</em> tasks until at most
    /// <see cref="_maxRetainedTerminalTasks"/> remain. Running tasks are never pruned. Each pruned
    /// task is dropped from the registry and order list, its version is bumped N =&gt; N+1 under the
    /// registry lock so the published <see cref="TaskChangeKind.Removed"/> change stays contiguous
    /// for a subscriber current at N, its per-consumer/process resources are released via
    /// <see cref="ManagedTask.Dispose"/>, and its log writer (if any) is closed — but its
    /// <em>persistent log file is preserved</em> for post-hoc diagnostics. The registry mutation
    /// runs under <see cref="_gate"/>; disposal and publication run outside it (matching
    /// <see cref="Remove"/>) so no disk I/O or subscriber callback happens under the lock.
    /// </summary>
    private void PruneTerminalTasks()
    {
        List<(string Id, long Version, ManagedTask Task)>? pruned = null;
        lock (_gate)
        {
            var terminalCount = 0;
            foreach (var t in _order)
            {
                if (t.Status != TaskRunStatus.Running) terminalCount++;
            }

            var index = 0;
            while (terminalCount > _maxRetainedTerminalTasks && index < _order.Count)
            {
                var t = _order[index];
                if (t.Status == TaskRunStatus.Running)
                {
                    index++; // never prune a running task; skip it and keep scanning older-first.
                    continue;
                }

                var version = t.BumpVersionForRemoval();
                _order.RemoveAt(index); // removed in place; do not advance index.
                (pruned ??= new()).Add((t.Id, version, t));
                terminalCount--;
            }
        }

        if (pruned is null) return;

        foreach (var (id, version, t) in pruned)
        {
            _tasks.TryRemove(id, out _);
            // Close the log writer if one somehow survived (terminal tasks close theirs above), but
            // never delete the persistent log file — it stays on disk for later inspection.
            if (_logs.TryRemove(id, out var log))
            {
                log.Dispose();
            }

            t.Dispose();
            Publish(id, version, TaskChangeKind.Removed);
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
    /// it), and a <see cref="TaskChangeKind.Removed"/> change is published. Removal atomically
    /// bumps the task's version from N to N+1 under the registry lock before dropping it, so the
    /// Removed change is contiguous for a subscriber current at N — it observes the removal
    /// without a spurious resync.
    /// </summary>
    public TaskActionResult Remove(string id)
    {
        ManagedTask task;
        long removedVersion;
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

            // Atomically bump the version (N => N+1) BEFORE removal so the Removed change is
            // contiguous with the version a subscriber already holds. The task is terminal, so no
            // other transition competes for the version; this bump is the removal's own event.
            removedVersion = task.BumpVersionForRemoval();
            _order.RemoveAt(index);
        }

        _tasks.TryRemove(id, out _);
        if (_logs.TryRemove(id, out var log))
        {
            log.Dispose();
        }

        // Publish the removal at the bumped version. The task is already terminal and now removed,
        // so no further change can follow it.
        task.Dispose();
        Publish(id, removedVersion, TaskChangeKind.Removed);
        return TaskActionResult.Ok;
    }

    /// <summary>
    /// Appends output to a task's ring and persistent log, then publishes an Output change
    /// carrying the EXACT version the append assigned. A no-op — no version bump, no log
    /// write, no notification, no waiter wake — when the id is unknown, the text is
    /// empty/null, or the task is already terminal. Output is attributed to
    /// <see cref="TaskOutputChannel.General"/>; use the channel overload for shell stdout/stderr.
    /// </summary>
    public void AppendOutput(string id, string text) =>
        AppendOutput(id, text, TaskOutputChannel.General);

    /// <summary>
    /// Appends output on a specific <paramref name="channel"/>. The in-memory ring stays a single
    /// raw combined stream (channel-agnostic), but the persistent log routes the text through the
    /// writer's independent per-channel redactor so interleaved stdout/stderr writes cannot
    /// corrupt or leak a secret straddling chunk boundaries on either stream.
    /// </summary>
    public void AppendOutput(string id, string text, TaskOutputChannel channel)
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
            log.Append(text, channel);
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

    /// <summary>The number of currently-registered live subscriptions (diagnostics/tests).</summary>
    internal int SubscriptionCount
    {
        get { lock (_gate) { return _subs.Count; } }
    }

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
        RaiseIdleStateChangedIfIdle();

        // Fire task-complete notification for background tasks (fire-and-forget).
        if (t.Mode == TaskExecutionMode.Background && this.NotificationCallback is { } cb)
        {
            _ = cb("task-complete", id);
        }

        // Enqueue the completion into the per-owner outbox so the owning agent learns about it
        // at its next iteration boundary without polling.
        this.EnqueueCompletionIfBackground(t, TaskRunStatus.Completed, result);
        this.SweepOutboxToLiveAncestor(id);

        return true;
    }

    /// <summary>Transitions a task to Failed and publishes a status change. Returns false if already terminal or unknown.</summary>
    public bool Fail(string id, string? error)
    {
        if (Find(id) is not { } t || !t.TryFail(error, out var version)) return false;
        Publish(id, version, TaskChangeKind.Status);
        RaiseIdleStateChangedIfIdle();

        // Enqueue failed workers too — Fail fires nothing today and an orchestrator most needs
        // to hear about failures.
        this.EnqueueCompletionIfBackground(t, TaskRunStatus.Failed, error);
        this.SweepOutboxToLiveAncestor(id);

        return true;
    }

    /// <summary>Transitions a task to Stopped and publishes a status change. Returns false if already terminal or unknown.</summary>
    public bool Stop(string id)
    {
        if (Find(id) is not { } t || !t.TryStop(out var version)) return false;
        Publish(id, version, TaskChangeKind.Status);
        RaiseIdleStateChangedIfIdle();

        // Enqueue stopped workers for the same reason as Fail above.
        this.EnqueueCompletionIfBackground(t, TaskRunStatus.Stopped, report: null);
        this.SweepOutboxToLiveAncestor(id);

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

    // -------------------------------------------------------------------------
    // Completion outbox — per-owner bounded queue of background-task terminals.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Enqueues a completion entry for <paramref name="task"/> when it is a background task.
    /// Foreground tasks skip this — their report is already the tool result and would be double-delivered.
    /// Resolves the effective owner via orphan roll-up: if the task's direct parent is already terminal
    /// (or pruned), the entry rolls up to the nearest live strict ancestor, ultimately landing on the
    /// main agent (null) so no completion is ever permanently lost.
    /// </summary>
    /// <remarks>
    /// MUST be called OUTSIDE <see cref="_gate"/> — same discipline as <see cref="Publish"/>.
    /// Uses a separate <see cref="_outboxGate"/> so neither lock nests inside the other.
    /// </remarks>
    private void EnqueueCompletionIfBackground(ManagedTask task, TaskRunStatus status, string? report)
    {
        if (task.Mode != TaskExecutionMode.Background) return;

        var entry = new TaskCompletionEntry(task.Id, task.Description, status, report);

        lock (this._outboxGate)
        {
            // Resolve owner INSIDE _outboxGate so observing the ancestor's status and enqueuing
            // are one atomic step. Without this, a parent could terminate between the resolve
            // (observing it as Running) and the enqueue, placing the child's entry in a dead
            // owner's outbox where nothing ever drains it. Lock ordering: _outboxGate -> ManagedTask._gate
            // (ResolveEffectiveOwner reads current.Status which locks ManagedTask._gate). There is no
            // reverse edge: TryComplete/TryFail/TryStop never take _outboxGate.
            var owner = this.ResolveEffectiveOwner(task.ParentId);
            if (!this._outbox.TryGetValue(OutboxKey(owner), out var queue))
            {
                queue = new Queue<TaskCompletionEntry>();
                this._outbox[OutboxKey(owner)] = queue;
            }

            // Enforce capacity: drop the oldest entry to keep the outbox bounded.
            if (queue.Count >= CompletionOutboxCapacity)
            {
                queue.Dequeue();
            }

            queue.Enqueue(entry);
        }
    }

    /// <summary>
    /// Sweeps any entries accumulated in <paramref name="terminatedId"/>'s outbox slot to the nearest
    /// live strict ancestor. Called after every terminal transition so that late-arriving child entries
    /// — enqueued between the parent's status flip and this sweep — are never stranded in a dead owner.
    /// Together with the atomic resolve+enqueue in <see cref="EnqueueCompletionIfBackground"/> this
    /// closes both halves of the concurrent parent+child termination race (spec §6.3).
    /// </summary>
    private void SweepOutboxToLiveAncestor(string terminatedId)
    {
        lock (this._outboxGate)
        {
            var key = OutboxKey(terminatedId);
            if (!this._outbox.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                this._outbox.Remove(key);
                return;
            }

            this._outbox.Remove(key);

            // The terminated task's parent is the correct starting point — the task itself is
            // already terminal so ResolveEffectiveOwner would skip it anyway.
            var terminated = this.Find(terminatedId);
            var liveAncestorId = this.ResolveEffectiveOwner(terminated?.ParentId);

            var targetKey = OutboxKey(liveAncestorId);
            if (!this._outbox.TryGetValue(targetKey, out var target))
            {
                target = new Queue<TaskCompletionEntry>();
                this._outbox[targetKey] = target;
            }

            foreach (var entry in queue)
            {
                if (target.Count >= CompletionOutboxCapacity)
                {
                    target.Dequeue();
                }

                target.Enqueue(entry);
            }
        }
    }

    /// <summary>
    /// Walks the ancestor chain from <paramref name="startId"/> and returns the first live
    /// (Running) ancestor's id, or <see langword="null"/> (main agent) when no running
    /// ancestor exists. The main agent is always considered live so the walk always terminates.
    /// </summary>
    private string? ResolveEffectiveOwner(string? startId)
    {
        // Main agent is always live — null is the terminal anchor.
        if (startId is null) return null;

        var current = Find(startId);
        if (current is null || current.Status == TaskRunStatus.Running)
        {
            // Either the owner exists and is live (normal case), or it has been pruned from the
            // registry (rare); treat pruned the same as dead and roll up.
            return current is not null ? startId : this.ResolveEffectiveOwner(null);
        }

        // Owner is terminal — roll up one level.
        return this.ResolveEffectiveOwner(current.ParentId);
    }

    /// <summary>
    /// Drains all completion entries for <paramref name="ownerTaskId"/> from the outbox and
    /// returns them. Delivery is EXACTLY ONCE: a second call for the same owner returns an
    /// empty list unless new completions arrived in the meantime.
    /// </summary>
    /// <param name="ownerTaskId">
    /// The task id of the owning agent, or <see langword="null"/> for the main agent.
    /// </param>
    public IReadOnlyList<TaskCompletionEntry> DrainCompletions(string? ownerTaskId)
    {
        lock (this._outboxGate)
        {
            if (!this._outbox.TryGetValue(OutboxKey(ownerTaskId), out var queue) || queue.Count == 0)
            {
                return Array.Empty<TaskCompletionEntry>();
            }

            var result = queue.ToArray();
            queue.Clear();
            return result;
        }
    }

    /// <summary>
    /// Removes the completion entry for <paramref name="taskId"/> from
    /// <paramref name="ownerTaskId"/>'s outbox. Used by <c>task_wait</c> when it returns a
    /// terminal result so the owning agent does not receive the same completion twice.
    /// </summary>
    /// <returns><see langword="true"/> if an entry was found and removed; <see langword="false"/> otherwise.</returns>
    public TaskCompletionEntry? ConsumeCompletion(string taskId, string? ownerTaskId)
    {
        var key = OutboxKey(ownerTaskId);
        lock (this._outboxGate)
        {
            if (!this._outbox.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                this._outbox.Remove(key);
                return null;
            }

            var rebuilt = new Queue<TaskCompletionEntry>(queue.Count);
            TaskCompletionEntry? found = null;
            foreach (var entry in queue)
            {
                if (found is null && entry.TaskId == taskId)
                {
                    found = entry;
                    continue;
                }

                rebuilt.Enqueue(entry);
            }

            if (rebuilt.Count == 0)
            {
                this._outbox.Remove(key);
            }
            else
            {
                this._outbox[key] = rebuilt;
            }

            return found;
        }
    }

    private bool IsIdleLocked() =>
        !_idleLeaseHeld &&
        !_shuttingDown &&
        !_disposed &&
        !_order.Any(task => task.Status == TaskRunStatus.Running);

    private void RaiseIdleStateChanged() => IdleStateChanged?.Invoke();

    private void RaiseIdleStateChangedIfIdle()
    {
        bool idle;
        lock (_gate)
        {
            idle = IsIdleLocked();
        }

        if (idle)
        {
            RaiseIdleStateChanged();
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
        bool becameBusy;
        lock (_gate)
        {
            // Idempotent: a second Dispose (e.g. after ShutdownAsync already disposed) is a no-op.
            if (_disposed) return;
            becameBusy = IsIdleLocked();
            _disposed = true;
            Monitor.PulseAll(_gate);

            tasks = _order.ToArray();
            // Snapshot AND clear subscriptions under the lock so no late publish reaches a
            // subscription after this point, then close them outside the lock (below).
            subs = _subs.ToArray();
            _subs.Clear();
        }

        if (becameBusy)
        {
            RaiseIdleStateChanged();
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
