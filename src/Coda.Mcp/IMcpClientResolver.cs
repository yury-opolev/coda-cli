namespace Coda.Mcp;

/// <summary>
/// Resolves the client that is <em>currently</em> serving a named MCP server.
/// <para>
/// This indirection exists because a server can be replaced underneath the tools that represent it.
/// A turn's tool registry is built once, at the start of the turn, so the wrappers the model calls
/// were created before any mid-turn restart. Binding them to a client instance would make every
/// retry after a restart talk to the process that was just killed — the restart would appear to
/// succeed and the server would stay broken until Coda itself was restarted. Resolving by name at
/// call time is what makes "restart, then retry" work within the same turn.
/// </para>
/// </summary>
public interface IMcpClientResolver
{
    /// <summary>
    /// The connected client for <paramref name="serverName"/>, or <see langword="null"/> when the
    /// server is not connected right now (stopped, or a restart that failed to come back).
    /// </summary>
    IMcpClient? ClientFor(string serverName);

    /// <summary>
    /// The metadata the connected server <em>currently</em> advertises for <paramref name="toolName"/>,
    /// or <see langword="null"/> when it no longer offers that tool.
    /// <para>
    /// A wrapper created before a restart carries the metadata of the server it was built from,
    /// including <see cref="Coda.Agent.ITool.IsReadOnly"/> — which decides whether the agent asks
    /// permission before running it. Following the server to a replacement must not also carry that
    /// classification across, so the caller re-checks it here against what is actually running.
    /// </para>
    /// </summary>
    McpToolInfo? AdvertisedToolFor(string serverName, string toolName);
}
