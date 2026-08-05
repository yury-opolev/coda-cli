using System.Collections.Immutable;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Mcp;

/// <summary>
/// An <see cref="ITableSource"/> projection over <see cref="ImmutableArray{McpServerSummary}"/> that
/// feeds the <see cref="TableView"/> in the MCP browser's list pane.
///
/// This type holds no I/O and no Terminal.Gui driver dependency — it can be created and asserted in
/// unit tests without initializing an application.
/// </summary>
internal sealed class McpServerTableSource : ITableSource
{
    private static readonly string[] ColumnNamesArray = ["Status", "Name", "Transport", "Scope", "Tools", "Error"];

    private readonly ImmutableArray<McpServerSummary> servers;
    private readonly StatusGlyphs glyphs;

    /// <param name="servers">The snapshot of servers to project.</param>
    /// <param name="glyphs">The glyph set to use for the Status column.</param>
    /// <param name="toolCounts">Reserved for future use; pass null. Tool counts are now stored on <see cref="McpServerSummary.ToolCount"/>.</param>
    public McpServerTableSource(
        ImmutableArray<McpServerSummary> servers,
        StatusGlyphs glyphs,
        object? toolCounts)
    {
        this.servers = servers.IsDefault ? [] : servers;
        this.glyphs = glyphs ?? StatusGlyphs.Unicode;
    }

    /// <inheritdoc/>
    public int Columns => ColumnNamesArray.Length;

    /// <inheritdoc/>
    public int Rows => this.servers.Length;

    /// <inheritdoc/>
    public string[] ColumnNames => ColumnNamesArray;

    /// <inheritdoc/>
    public object this[int row, int col]
    {
        get
        {
            var server = this.servers[row];
            return col switch
            {
                0 => this.glyphs[GetState(server)],
                1 => SafeSingle(server.Key.Name),
                2 => server.Transport == McpTransportKind.Http ? "http" : "stdio",
                3 => server.Key.Scope == McpConfigScope.User ? "user" : "project",
                4 => server.ToolCount.HasValue ? server.ToolCount.Value.ToString() : "",
                5 => SafeSingle(server.LastError ?? string.Empty),
                _ => string.Empty,
            };
        }
    }

    /// <summary>
    /// Maps a <see cref="McpServerSummary"/> to a <see cref="BrowserItemState"/> using the
    /// prescribed precedence: Overridden > Disabled > Error > Healthy > Idle.
    /// </summary>
    public static BrowserItemState GetState(McpServerSummary summary)
    {
        if (!summary.IsEffective)
        {
            return BrowserItemState.Overridden;
        }

        if (!summary.Enabled)
        {
            return BrowserItemState.Disabled;
        }

        if (summary.Connection == McpConnectionState.Error)
        {
            return BrowserItemState.Error;
        }

        if (summary.Connection == McpConnectionState.Connected)
        {
            return BrowserItemState.Healthy;
        }

        return BrowserItemState.Idle;
    }

    /// <summary>Returns the <see cref="McpServerSummary"/> at <paramref name="rowIndex"/>.</summary>
    public McpServerSummary SummaryAt(int rowIndex) => this.servers[rowIndex];

    private static string SafeSingle(string value) => TerminalTextSanitizer.SanitizeSingleLine(value);
}
