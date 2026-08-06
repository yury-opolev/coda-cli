namespace Coda.Mcp;

/// <summary>The outcome of connecting a single MCP server.</summary>
/// <param name="SchemaWarning">
/// A user-facing line describing input schemas that had to be repaired, or null when every
/// advertised schema was usable. See <see cref="McpSchemaPolicyFilter.DescribeCoercions"/>.
/// </param>
public sealed record McpConnectResult(bool Connected, int ToolCount, string? Error, string? SchemaWarning = null)
{
    public static McpConnectResult Success(int toolCount, string? schemaWarning = null) =>
        new(true, toolCount, null, schemaWarning);

    public static McpConnectResult Failure(string error) => new(false, 0, error);
}
