namespace Coda.Mcp;

/// <summary>The outcome of connecting a single MCP server.</summary>
public sealed record McpConnectResult(bool Connected, int ToolCount, string? Error)
{
    public static McpConnectResult Success(int toolCount) => new(true, toolCount, null);

    public static McpConnectResult Failure(string error) => new(false, 0, error);
}
