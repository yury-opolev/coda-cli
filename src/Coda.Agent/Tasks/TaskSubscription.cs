namespace Coda.Agent.Tasks;

/// <summary>
/// A single consumer's view of task changes. Receives an immutable initial
/// snapshot at creation, then bounded drop-oldest change notifications. When the
/// queue overflows, the oldest change is dropped and the next <see cref="Drain"/>
/// reports <c>ResyncRequired = true</c> so the consumer re-reads manager snapshots.
/// Producers never block: <see cref="Post"/> only enqueues and signals.
/// Disposing unsubscribes from the manager and makes further posts no-ops so a
/// forgotten subscriber cannot leak or accumulate changes.
/// </summary>
public sealed class TaskSubscription : IDisposable
{
    public const int DefaultCapacity = 1024;

    private readonly object _gate = new();
    private readonly Queue<TaskChange> _queue = new();
    private readonly int _capacity;
    private readonly Action<TaskSubscription>? _onDispose;
    private bool _gap;
    private bool _closed;
    private TaskCompletionSource _signal = NewSignal();

    public TaskSubscription(IReadOnlyList<TaskSnapshot> initialSnapshot, int capacity = DefaultCapacity)
        : this(initialSnapshot, capacity, onDispose: null)
    {
    }

    internal TaskSubscription(
        IReadOnlyList<TaskSnapshot> initialSnapshot,
        int capacity,
        Action<TaskSubscription>? onDispose)
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
        _onDispose = onDispose;
    }

    /// <summary>The complete task list captured when this subscription was created.</summary>
    public IReadOnlyList<TaskSnapshot> InitialSnapshot { get; }

    /// <summary>Enqueues a change (drop-oldest on overflow) and wakes any waiter. Never blocks.</summary>
    public void Post(TaskChange change)
    {
        lock (_gate)
        {
            // Late posts after dispose are ignored so a torn-down consumer cannot leak.
            if (_closed)
            {
                return;
            }

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
    /// Removes and returns all pending changes in order, plus whether a gap occurred
    /// (meaning the consumer must resynchronize from manager snapshots).
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

    /// <summary>Closes the subscription, unsubscribes from the manager, and wakes any waiter. Idempotent.</summary>
    public void Dispose()
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

        // Invoke outside the subscription lock: the manager takes its own lock in
        // Unsubscribe, so calling it under _gate would invert lock ordering.
        _onDispose?.Invoke(this);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
