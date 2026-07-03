namespace Coda.Mcp;

/// <summary>
/// Base type for a configured MCP server (from <c>.mcp.json</c>). Concrete kinds are
/// <see cref="McpStdioServerConfig"/> (a locally launched process) and
/// <see cref="McpHttpServerConfig"/> (a remote Streamable-HTTP endpoint).
/// </summary>
public abstract record McpServerConfig
{
    /// <summary>
    /// When true (<c>"disabled": true</c> in <c>.mcp.json</c>), the server is not auto-connected at
    /// startup (excluded from <see cref="McpConfig.Load"/>), but still listed by
    /// <see cref="McpConfig.LoadEntries"/> so <c>/mcp</c> can show and re-enable it.
    /// </summary>
    public bool Disabled { get; init; }
}
