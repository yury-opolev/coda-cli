namespace Coda.Mcp;

/// <summary>A stdio MCP server launched as a local process (<c>type:"stdio"</c> or none).</summary>
public sealed record McpStdioServerConfig(
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env) : McpServerConfig;
