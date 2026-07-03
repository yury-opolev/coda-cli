namespace Coda.Mcp;

/// <summary>Which layer a configured MCP server came from.</summary>
public enum McpConfigScope
{
    /// <summary>User-level <c>~/.coda/.mcp.json</c> (or <c>CODA_USER_MCP_DIR</c>).</summary>
    User = 0,

    /// <summary>Project-level <c>&lt;cwd&gt;/.mcp.json</c> — overrides a user entry of the same name.</summary>
    Project = 1,
}
