using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Coda.Tui.Ui.Events;

/// <summary>
/// A bounded, coalescing mailbox that decouples many event producers from a single UI reader.
/// Streaming events for the same logical stream collapse into one queued node so a slow reader
/// never sees unbounded backlog: assistant text deltas concatenate, tool progress keeps the latest
/// value. When the queue is full a new coalescible event or a critical (non-coalescible) event
/// evicts the oldest coalescible node; only a queue consisting exclusively of critical events forces
/// a producer to wait for the reader to make room. The queue length never exceeds the capacity.
/// </summary>
public sealed class UiEventMailbox : IUiEventPublisher, IDisposable
{
    private const string AssistantKey = "assistant";

    private readonly int _capacity;
    private readonly CancellationToken _hostCancellationToken;
    private readonly LinkedList<UiEvent> _queue = new();
    private readonly Dictionary<string, LinkedListNode<UiEvent>> _coalesce = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _items = new(0);
    private readonly SemaphoreSlim _spaces = new(0);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CancellationToken _disposeToken;
    private int _waitingProducers;
    private bool _disposed;

    /// <summary>Create a mailbox holding at most <paramref name="capacity"/> queued events.</summary>
    /// <param name="capacity">Maximum number of queued events; must be at least one.</param>
    /// <param name="hostCancellationToken">Cancels any blocked producer when the host shuts down.</param>
    public UiEventMailbox(int capacity, CancellationToken hostCancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _hostCancellationToken = hostCancellationToken;
        _disposeToken = _disposeCts.Token;
    }

    /// <summary>The current number of queued events; always between zero and the capacity.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    /// <inheritdoc />
    public void Publish(UiEvent uiEvent)
    {
        ArgumentNullException.ThrowIfNull(uiEvent);

        while (true)
        {
            lock (_lock)
            {
                ThrowIfDisposed();

                var key = CoalesceKey(uiEvent);
                if (key is not null && _coalesce.TryGetValue(key, out var existing))
                {
                    existing.Value = Merge(existing.Value, uiEvent);
                    return;
                }

                if (_queue.Count < _capacity)
                {
                    Enqueue(uiEvent, key);
                    _items.Release();
                    return;
                }

                var victim = FindOldestCoalescible();
                if (victim is not null)
                {
                    RemoveNode(victim);
                    Enqueue(uiEvent, key);
                    return;
                }

                _waitingProducers++;
            }

            try
            {
                WaitForSpace();
            }
            finally
            {
                lock (_lock)
                {
                    _waitingProducers--;
                }
            }
        }
    }

    /// <summary>Remove and return the oldest event, waiting until one is available.</summary>
    public async ValueTask<UiEvent> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeToken);
        try
        {
            await _items.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            ThrowIfDisposed();
            throw;
        }

        lock (_lock)
        {
            ThrowIfDisposed();
            var node = _queue.First!;
            var value = node.Value;
            RemoveNode(node);
            SignalSpaceFreed();
            return value;
        }
    }

    /// <summary>Remove and return the oldest event without waiting; returns false when empty.</summary>
    public bool TryRead(out UiEvent? uiEvent)
    {
        uiEvent = null;

        bool acquired;
        try
        {
            acquired = _items.Wait(0);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        if (!acquired)
        {
            return false;
        }

        lock (_lock)
        {
            if (_disposed || _queue.Count == 0)
            {
                return false;
            }

            var node = _queue.First!;
            uiEvent = node.Value;
            RemoveNode(node);
            SignalSpaceFreed();
            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Wake every blocked reader and producer through cancellation. The semaphores are left
        // undisposed on purpose: disposing a SemaphoreSlim while an async waiter is still
        // completing its cancellation can abandon that waiter and deadlock the caller. They own no
        // unmanaged handle here (AvailableWaitHandle is never touched), so cancellation is a clean,
        // race-free shutdown.
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    private static string? CoalesceKey(UiEvent uiEvent) => uiEvent switch
    {
        AssistantTextDeltaEvent => AssistantKey,
        ToolProgressEvent tool => "tool:" + tool.ToolName,
        _ => null,
    };

    private static UiEvent Merge(UiEvent existing, UiEvent incoming) => (existing, incoming) switch
    {
        (AssistantTextDeltaEvent a, AssistantTextDeltaEvent b) => new AssistantTextDeltaEvent(a.Delta + b.Delta),
        _ => incoming,
    };

    private void Enqueue(UiEvent uiEvent, string? key)
    {
        var node = _queue.AddLast(uiEvent);
        if (key is not null)
        {
            _coalesce[key] = node;
        }
    }

    private LinkedListNode<UiEvent>? FindOldestCoalescible()
    {
        for (var node = _queue.First; node is not null; node = node.Next)
        {
            if (CoalesceKey(node.Value) is not null)
            {
                return node;
            }
        }

        return null;
    }

    private void RemoveNode(LinkedListNode<UiEvent> node)
    {
        var key = CoalesceKey(node.Value);
        if (key is not null && _coalesce.TryGetValue(key, out var mapped) && ReferenceEquals(mapped, node))
        {
            _coalesce.Remove(key);
        }

        _queue.Remove(node);
    }

    private void SignalSpaceFreed()
    {
        if (_waitingProducers > 0 && _spaces.CurrentCount < _waitingProducers)
        {
            _spaces.Release();
        }
    }

    private void WaitForSpace()
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_hostCancellationToken, _disposeToken);
        try
        {
            _spaces.Wait(linked.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            ThrowIfDisposed();
            throw;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed))
        {
            throw new ObjectDisposedException(nameof(UiEventMailbox));
        }
    }
}
