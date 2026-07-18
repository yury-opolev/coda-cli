namespace Coda.Mcp;

/// <summary>An immutable, UI-facing view of one MCP server's identity, reported info and tool count.</summary>
public sealed record McpServerRuntimeSnapshot(string Name, McpServerInfo? Info, int ToolCount);

/// <summary>An immutable, versioned, UI-facing view of the MCP runtime and its servers.</summary>
public sealed record McpRuntimeSnapshot(int Version, IReadOnlyList<McpServerRuntimeSnapshot> Servers);
