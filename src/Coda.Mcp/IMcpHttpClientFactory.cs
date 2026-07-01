namespace Coda.Mcp;

/// <summary>
/// Builds an <see cref="IMcpClient"/> for an HTTP MCP server. The host supplies the
/// implementation so that transport-level dependencies it owns — an
/// <see cref="System.Net.Http.HttpClient"/>, the token store, a browser opener, and
/// whether the run is interactive — are injected without <c>Coda.Mcp</c> taking them on.
/// </summary>
public interface IMcpHttpClientFactory
{
    IMcpClient Create(string serverName, McpHttpServerConfig config);
}
