namespace Coda.Agent.Tasks;

/// <summary>
/// A single consumer's view of task changes, created by <see cref="TaskManager.Subscribe"/>
/// (the manager is the only factory; construction and <see cref="Post"/> are internal).
///
/// The consumer receives an immutable initial snapshot at creation, then bounded
/// drop-oldest change notifications. Each change carries the exact task version assigned by
/// the manager at publish time. The subscription tracks the last version it observed per
/// task (seeded from <see cref="InitialSnapshot"/>) and reports <c>ResyncRequired = true</c>
/// from the next <see cref="Drain"/> whenever it detects an inconsistency:
/// <list type="bullet">
/// <item>a version gap (a skipped version for a task);</item>
/// <item>a duplicate or out-of-order version (not exactly last + 1);</item>
/// <item>a first change for a task whose creation it never observed;</item>
/// <item>a queue overflow that dropped the oldest change.</item>
/// </list>
///
/// Change <em>kind</em> ordering across tasks may race with the manager's per-task version
/// assignment (versions are bumped outside the registry lock, then published under it), so a
/// consumer may occasionally see a conservative false resync. That is by design: any
/// inconsistency degrades safely to a resync, and manager snapshots are always authoritative
/// — the consumer re-reads them on resync rather than trusting the incremental stream.
///
/// Producers never block: <see cref="Post"/> only enqueues and signals. Closing (via
/// <see cref="Dispose"/>, or when the owning manager is disposed) makes further posts
/// no-ops, wakes any pending <see cref="WaitAsync"/>, and flips <see cref="IsClosed"/> so
/// consumers can exit cleanly.
/// </summary>
public sealed class TaskSubscription : IDisposable
{
    public const int DefaultCapacity = 1024;

    private readonly object _gate = new();
    private readonly Queue<TaskChange> _queue = new();
    // Last version observed per task; seeded from the initial snapshot so the very first
    // change per task is validated against the state the consumer already holds.
    private readonly Dictionary<string, long> _lastVersion = new();
    private readonly int _capacity;
    private readonly Action<TaskSubscription>? _onClose;
    private bool _gap;
    private bool _closed;
    private TaskCompletionSource _signal = NewSignal();

    internal TaskSubscription(
        IReadOnlyList<TaskSnapshot> initialSnapshot,
        int capacity = DefaultCapacity,
        Action<TaskSubscription>? onClose = null)
    {
        ArgumentNullException.ThrowIfNull(initialSnapshot);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        // Defensive copy: the captured snapshot must never change under the consumer,
        // regardless of what the caller does with the list it passed in.
        InitialSnapshot = initialSnapshot.ToArray();
        _capacity = capacity;
        _onClose = onClose;

        // Seed last-seen versions so a task present in the snapshot expects its next change
        // at snapshot.Version + 1; anything else is a gap/duplicate/reorder → resync.
        foreach (var snap in InitialSnapshot)
        {
            _lastVersion[snap.Id] = snap.Version;
        }
    }

    /// <summary>The complete task list captured when this subscription was created.</summary>
    public IReadOnlyList<TaskSnapshot> InitialSnapshot { get; }

    /// <summary>
    /// True once the subscription is closed (explicitly disposed or via manager shutdown).
    /// A closed subscription drops posts and completes every <see cref="WaitAsync"/>.
    /// </summary>
    public bool IsClosed
    {
        get { lock (_gate) { return _closed; } }
    }

    /// <summary>
    /// Enqueues a change (drop-oldest on overflow), records any version inconsistency as a
    /// pending resync, and wakes any waiter. Never blocks. Internal: the manager is the sole
    /// producer.
    /// </summary>
    internal void Post(TaskChange change)
    {
        lock (_gate)
        {
            // Late posts after close are ignored so a torn-down consumer cannot leak.
            if (_closed)
            {
                return;
            }

            TrackVersion(change);

            if (_queue.Count >= _capacity)
            {
                _queue.Dequeue();
                _gap = true;
            }

            _queue.Enqueue(change);
            _signal.TrySetResult();
        }
    }

    /// <summary>
    /// Validates <paramref name="change"/> against the last version seen for its task and
    /// flags a resync on any inconsistency. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void TrackVersion(TaskChange change)
    {
        var known = _lastVersion.TryGetValue(change.TaskId, out var last);

        if (change.Kind == TaskChangeKind.Created)
        {
            // A Created for a task we already track is a duplicate/reorder: the manager
            // publishes exactly one Created per task and never to a subscriber that already
            // has it in its snapshot.
            if (known)
            {
                _gap = true;
            }
        }
        else if (!known)
        {
            // First change for a task we never saw created or snapshotted: we missed its
            // birth, so the incremental view is incomplete.
            _gap = true;
        }
        else if (change.Version != last + 1)
        {
            // Skipped, duplicated, or out-of-order version.
            _gap = true;
        }

        _lastVersion[change.TaskId] = change.Version;
    }

    /// <summary>
    /// Removes and returns all pending changes in order, plus whether a resync is required
    /// (a version gap/duplicate/reorder or a queue overflow occurred). On resync the consumer
    /// must re-read manager snapshots, which are authoritative.
    /// </summary>
    public (IReadOnlyList<TaskChange> Changes, bool ResyncRequired) Drain()
    {
        lock (_gate)
        {
            var items = _queue.ToArray();
            _queue.Clear();
            var hadGap = _gap;
            _gap = false;
            _signal = NewSignal();
            return (items, hadGap);
        }
    }

    /// <summary>Completes when at least one change is pending or the subscription is closed. Cancellable.</summary>
    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_closed || _queue.Count > 0 || _gap)
            {
                return Task.CompletedTask;
            }

            return _signal.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Closes the subscription, unsubscribes from the manager, and wakes any waiter.
    /// Idempotent. This is the only public teardown path, so callers cannot detach a
    /// subscription while leaving a waiter hanging.
    /// </summary>
    public void Dispose() => CloseCore(invokeOnClose: true);

    /// <summary>
    /// Closes the subscription WITHOUT invoking the manager unsubscribe callback. Used by the
    /// manager during its own disposal, which has already removed the subscription from its
    /// list, to avoid a re-entrant callback. Idempotent; wakes any waiter.
    /// </summary>
    internal void Close() => CloseCore(invokeOnClose: false);

    private void CloseCore(bool invokeOnClose)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _queue.Clear();
            _gap = false;
            // Release any pending waiter so callers observing WaitAsync do not hang.
            _signal.TrySetResult();
        }

        // Invoke outside the subscription lock: the manager takes its own lock in the
        // callback, so calling it under _gate would invert lock ordering.
        if (invokeOnClose)
        {
            _onClose?.Invoke(this);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
