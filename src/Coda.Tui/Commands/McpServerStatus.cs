using Coda.Mcp;

namespace Coda.Tui.Commands;

/// <summary>
/// The display state of one configured MCP server: its config entry (name/transport/scope), whether
/// it is currently connected, the identity it reported at initialize, and its connected tools.
/// A plain data snapshot so the <see cref="McpView"/> formatter is pure and testable.
/// </summary>
public sealed record McpServerStatus(
    McpServerEntry Entry,
    bool Connected,
    McpServerInfo? Info,
    IReadOnlyList<McpToolLine> Tools);
