namespace Coda.Mcp.Auth;

/// <summary>The outcome of a user-initiated MCP OAuth reauthentication attempt.</summary>
public sealed record McpAuthResult(bool Succeeded, string? Error);

/// <summary>Starts proactive OAuth reauthentication for an HTTP MCP server.</summary>
public interface IMcpOAuthReauthenticator
{
    Task<McpAuthResult> ReauthenticateAsync(
        McpHttpServerConfig config,
        CancellationToken cancellationToken = default);
}
