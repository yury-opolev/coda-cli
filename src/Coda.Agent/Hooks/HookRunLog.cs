namespace Coda.Agent.Hooks;

/// <summary>
/// Session-scoped, in-memory log of hook last-run records. One entry per hook, keyed
/// by the hook's 0-based index in the session's configured hook list. Never persisted.
/// </summary>
/// <remarks>
/// Thread-safe (synchronised on an internal lock). The log is populated by
/// <see cref="HookBus"/> during normal hook execution.
/// </remarks>
public sealed class HookRunLog
{
    private readonly Dictionary<int, HookRunEntry> entries = [];
    private readonly object sync = new();

    /// <summary>Records a run entry for the hook at <paramref name="hookIndex"/>.</summary>
    public void Record(int hookIndex, HookRunEntry entry)
    {
        lock (this.sync)
        {
            this.entries[hookIndex] = entry;
        }
    }

    /// <summary>
    /// Returns the most recent run entry for the hook at <paramref name="hookIndex"/>,
    /// or <see langword="null"/> if the hook has not run in this session.
    /// </summary>
    public HookRunEntry? Get(int hookIndex)
    {
        lock (this.sync)
        {
            return this.entries.TryGetValue(hookIndex, out var entry) ? entry : null;
        }
    }
}
