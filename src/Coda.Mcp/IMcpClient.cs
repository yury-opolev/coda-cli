using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coda.Mcp;

/// <summary>
/// A connection to one MCP server, independent of transport. Implemented by
/// <see cref="McpStdioClient"/> (local process) and <see cref="McpHttpClient"/> (remote
/// Streamable HTTP). The <see cref="McpClientManager"/> and tool adapters depend on this
/// abstraction so the two transports are interchangeable.
/// </summary>
public interface IMcpClient : IAsyncDisposable
{
    /// <summary>The configured server name (used to build <c>mcp__{server}__{tool}</c> names).</summary>
    string ServerName { get; }

    /// <summary>
    /// Identity the server reported at <c>initialize</c> (name/version/instructions), or null before
    /// initialize or when the server reported none. Default null so simple implementers need not set it.
    /// </summary>
    McpServerInfo? ServerInfo => null;

    /// <summary>Run the initialize handshake and return the server's tools.</summary>
    Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Invoke a tool and return its formatted result text and error flag.</summary>
    Task<(string Text, bool IsError)> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default);

    /// <summary>List the server's resources (empty when unsupported).</summary>
    Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>Read a resource by URI.</summary>
    Task<string> ReadResourceAsync(string uri, CancellationToken cancellationToken = default);

    /// <summary>List the server's prompts (empty when unsupported).</summary>
    Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken cancellationToken = default);

    /// <summary>Get a rendered prompt by name.</summary>
    Task<string> GetPromptAsync(string name, JsonNode? arguments, CancellationToken cancellationToken = default);
}
