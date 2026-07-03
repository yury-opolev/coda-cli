namespace Coda.Mcp;

/// <summary>A configured MCP server plus the layer it was resolved from (for display / scope-aware edits).</summary>
public sealed record McpServerEntry(string Name, McpServerConfig Config, McpConfigScope Scope);
