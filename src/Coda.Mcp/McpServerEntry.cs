namespace Coda.Mcp;

/// <summary>A configured MCP server plus the layer it was resolved from (for display / scope-aware edits).</summary>
public sealed record McpServerEntry(string Name, McpServerConfig Config, McpConfigScope Scope)
{
    /// <summary>
    /// The name of the plugin that contributed this server, or <see langword="null"/> for
    /// user and project entries. Used by <c>/mcp list</c> to make the origin traceable.
    /// </summary>
    public string? PluginName { get; init; }
}
