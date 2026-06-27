using LlmClient;

namespace Coda.Agent;

/// <summary>Holds the available tools and exposes their wire definitions.</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> byName = new(StringComparer.Ordinal);

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (var tool in tools)
        {
            this.byName[tool.Name] = tool;
        }
    }

    public ITool? Resolve(string name) => this.byName.TryGetValue(name, out var tool) ? tool : null;

    public IReadOnlyList<ITool> All => [.. this.byName.Values];

    public IReadOnlyList<ToolDefinition> Definitions => [.. this.byName.Values.Select(t => t.ToDefinition())];

    /// <summary>Returns a new registry containing only the read-only tools.</summary>
    public ToolRegistry ReadOnly() => new(this.byName.Values.Where(t => t.IsReadOnly));
}
