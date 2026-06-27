namespace Coda.Agent;

/// <summary>Holds the current session todo list. Thread-safe replacement of the whole list.</summary>
public sealed class TodoStore
{
    private readonly object gate = new();
    private IReadOnlyList<TodoItem> items = [];

    public IReadOnlyList<TodoItem> Items
    {
        get
        {
            lock (this.gate)
            {
                return this.items;
            }
        }
    }

    public void Set(IReadOnlyList<TodoItem> newItems)
    {
        ArgumentNullException.ThrowIfNull(newItems);
        lock (this.gate)
        {
            this.items = [.. newItems];
        }
    }
}
