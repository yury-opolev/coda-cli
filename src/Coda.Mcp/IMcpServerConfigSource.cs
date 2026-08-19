namespace Coda.Mcp;

/// <summary>
/// Supplies the MCP server definitions a live session was configured from — including entries that
/// are explicitly disabled — so a lifecycle operation can tell "not configured at all" apart from
/// "configured but disabled". <see cref="McpConfig.Load"/> deliberately drops disabled entries, so
/// it cannot answer that question on its own.
/// </summary>
public interface IMcpServerConfigSource
{
    /// <summary>
    /// The effective server definitions merged across every configuration layer, keyed ordinally by
    /// server name, with disabled entries INCLUDED. Re-read on every call so a configuration edit
    /// made after startup (e.g. via <c>/mcp</c>) is picked up.
    /// </summary>
    /// <exception cref="McpException">A configuration file exists but is malformed.</exception>
    IReadOnlyDictionary<string, McpServerConfig> LoadServers();
}
