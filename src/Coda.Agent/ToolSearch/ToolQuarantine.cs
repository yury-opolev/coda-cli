using LlmClient;

namespace Coda.Agent.ToolSearch;

/// <summary>
/// The set of tools withheld from the wire for the rest of the session because the model API
/// rejected their definitions.
/// </summary>
/// <remarks>
/// Session-scoped and applied to <em>every</em> path that builds wire definitions — including
/// Standard mode, which does not go through <see cref="ToolSearchCoordinator"/> at all. Without
/// that, a tool the provider refuses would be re-sent on every subsequent request and no turn
/// could ever succeed again.
/// </remarks>
public sealed class ToolQuarantine
{
    private readonly HashSet<string> names = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    /// <summary>Quarantines <paramref name="name"/>. Returns false when it was already quarantined.</summary>
    public bool Add(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (this.gate)
        {
            return this.names.Add(name);
        }
    }

    /// <summary>True when <paramref name="name"/> is quarantined.</summary>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (this.gate)
        {
            return this.names.Contains(name);
        }
    }

    /// <summary>A snapshot of the quarantined tool names.</summary>
    public IReadOnlyCollection<string> Names
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.names];
            }
        }
    }

    /// <summary>Number of tools currently quarantined.</summary>
    public int Count
    {
        get
        {
            lock (this.gate)
            {
                return this.names.Count;
            }
        }
    }

    /// <summary>
    /// Removes quarantined tools from <paramref name="definitions"/>. Returns the input instance
    /// unchanged when nothing is quarantined, which is the overwhelmingly common case.
    /// </summary>
    public IReadOnlyList<ToolDefinition> Filter(IReadOnlyList<ToolDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        lock (this.gate)
        {
            if (this.names.Count == 0)
            {
                return definitions;
            }

            return [.. definitions.Where(d => !this.names.Contains(d.Name))];
        }
    }
}
