namespace Coda.Mcp;

/// <summary>Identifies a physical MCP server definition by its configuration scope and name.</summary>
public readonly record struct McpServerKey(McpConfigScope Scope, string Name);
