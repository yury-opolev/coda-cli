namespace Coda.Mcp;

/// <summary>A physical MCP server definition and whether it wins scope precedence.</summary>
public sealed record McpPhysicalServerEntry(
    McpServerKey Key,
    McpServerConfig Config,
    string SourceFile,
    bool IsEffective);
