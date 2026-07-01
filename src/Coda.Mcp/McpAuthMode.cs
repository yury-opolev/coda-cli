namespace Coda.Mcp;

/// <summary>How Coda authenticates to an HTTP MCP server.</summary>
public enum McpAuthMode
{
    /// <summary>Never attach an Authorization header.</summary>
    None,

    /// <summary>Attach a static <c>Authorization: Bearer &lt;token&gt;</c> header.</summary>
    Bearer,

    /// <summary>Run the MCP OAuth flow (discovery + PKCE) on a 401 challenge.</summary>
    OAuth,
}
