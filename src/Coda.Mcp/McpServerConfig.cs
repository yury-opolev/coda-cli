namespace Coda.Mcp;

/// <summary>
/// Base type for a configured MCP server (from <c>.mcp.json</c>). Concrete kinds are
/// <see cref="McpStdioServerConfig"/> (a locally launched process) and
/// <see cref="McpHttpServerConfig"/> (a remote Streamable-HTTP endpoint).
/// </summary>
public abstract record McpServerConfig;
