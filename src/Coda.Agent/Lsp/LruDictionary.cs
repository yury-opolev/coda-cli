namespace Coda.Agent.Lsp;

/// <summary>
/// A bounded dictionary that evicts the least-recently-used entry when the
/// capacity is exceeded.  "Used" means any Get or Set access.
/// Not thread-safe.
/// </summary>
internal sealed class LruDictionary<TKey, TValue>
    where TKey : notnull
{
    private readonly int capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> map;
    private readonly LinkedList<(TKey Key, TValue Value)> order;

    public LruDictionary(int capacity)
    {
        this.capacity = capacity;
        this.map = new Dictionary<TKey, LinkedListNode<(TKey, TValue)>>(capacity + 1);
        this.order = new LinkedList<(TKey, TValue)>();
    }

    public bool TryGetValue(TKey key, out TValue? value)
    {
        if (!this.map.TryGetValue(key, out var node))
        {
            value = default;
            return false;
        }

        this.MoveToFront(node);
        value = node.Value.Value;
        return true;
    }

    public void Set(TKey key, TValue value)
    {
        if (this.map.TryGetValue(key, out var existing))
        {
            this.order.Remove(existing);
            this.map.Remove(key);
        }

        var node = this.order.AddFirst((key, value));
        this.map[key] = node;

        if (this.map.Count > this.capacity)
        {
            this.EvictLast();
        }
    }

    public void Remove(TKey key)
    {
        if (!this.map.TryGetValue(key, out var node))
        {
            return;
        }

        this.order.Remove(node);
        this.map.Remove(key);
    }

    public void Clear()
    {
        this.map.Clear();
        this.order.Clear();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private void MoveToFront(LinkedListNode<(TKey Key, TValue Value)> node)
    {
        this.order.Remove(node);
        this.order.AddFirst(node);
    }

    private void EvictLast()
    {
        var last = this.order.Last;
        if (last is null)
        {
            return;
        }

        this.map.Remove(last.Value.Key);
        this.order.RemoveLast();
    }
}
