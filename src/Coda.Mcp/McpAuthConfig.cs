namespace Coda.Mcp;

/// <summary>
/// Authentication settings for an HTTP MCP server (the <c>auth</c> block of an HTTP
/// entry in <c>.mcp.json</c>). When omitted, a server defaults to <see cref="McpAuthMode.OAuth"/>
/// so that a 401 challenge transparently triggers the discovery flow.
/// </summary>
public sealed record McpAuthConfig(
    McpAuthMode Mode,
    string? ClientId = null,
    IReadOnlyList<string>? Scopes = null,
    string? BearerToken = null)
{
    /// <summary>The default for an HTTP server with no explicit <c>auth</c> block.</summary>
    public static McpAuthConfig Default { get; } = new(McpAuthMode.OAuth);
}
