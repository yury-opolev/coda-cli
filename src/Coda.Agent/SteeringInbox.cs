namespace Coda.Agent;

/// <summary>An operator message queued for delivery into an active agent turn.</summary>
public sealed record SteeringEntry(string Id, string Text, DateTimeOffset EnqueuedAt);

/// <summary>
/// Thread-safe FIFO queue for operator steering. The queue is open for an active turn and is
/// atomically sealed at its natural completion, preventing a racing message from being accepted
/// after the last delivery boundary.
/// </summary>
public sealed class SteeringInbox
{
    private readonly object gate = new();
    private readonly List<SteeringEntry> pending = [];
    private bool sealedEmpty;

    /// <summary>Gets whether the queue currently contains undelivered messages.</summary>
    public bool HasPending
    {
        get
        {
            lock (this.gate)
            {
                return this.pending.Count != 0;
            }
        }
    }

    /// <summary>
    /// Queues a message, returning its accepted entry, or <see langword="null"/> if the owning
    /// turn has already sealed its queue.
    /// </summary>
    public SteeringEntry? Enqueue(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (this.gate)
        {
            if (this.sealedEmpty)
            {
                return null;
            }

            var entry = new SteeringEntry(Guid.NewGuid().ToString("N"), text, DateTimeOffset.UtcNow);
            this.pending.Add(entry);
            return entry;
        }
    }

    /// <summary>Atomically removes pending entries for delivery, preserving FIFO order.</summary>
    public IReadOnlyList<SteeringEntry> TakeAllForDelivery() => this.TakeAll();

    /// <summary>Atomically removes pending entries for recall, preserving FIFO order.</summary>
    public IReadOnlyList<SteeringEntry> RecallAll() => this.TakeAll();

    /// <summary>Legacy text-only delivery API. New consumers should use <see cref="TakeAllForDelivery"/>.</summary>
    public IReadOnlyList<string> DrainAll() => this.TakeAll().Select(entry => entry.Text).ToArray();

    /// <summary>Reopens the queue for a newly-started turn without discarding queued entries.</summary>
    public void OpenForTurn()
    {
        lock (this.gate)
        {
            this.sealedEmpty = false;
        }
    }

    /// <summary>Clears pending entries and reopens the queue.</summary>
    public void Clear()
    {
        lock (this.gate)
        {
            this.pending.Clear();
            this.sealedEmpty = false;
        }
    }

    /// <summary>
    /// Atomically seals the queue only if it is empty. A false result leaves the queue open so
    /// the caller can deliver the raced message at another safe boundary.
    /// </summary>
    public bool TrySealEmpty()
    {
        lock (this.gate)
        {
            if (this.pending.Count != 0)
            {
                return false;
            }

            this.sealedEmpty = true;
            return true;
        }
    }

    private IReadOnlyList<SteeringEntry> TakeAll()
    {
        lock (this.gate)
        {
            if (this.pending.Count == 0)
            {
                return [];
            }

            var entries = this.pending.ToArray();
            this.pending.Clear();
            return entries;
        }
    }
}
