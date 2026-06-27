namespace Coda.Mcp;

/// <summary>A configured stdio MCP server (from <c>.mcp.json</c>).</summary>
public sealed record McpServerConfig(string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env);
