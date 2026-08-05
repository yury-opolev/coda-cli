using Coda.Agent.Tasks;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Tasks;

/// <summary>
/// An <see cref="ITableSource"/> projection over a <see cref="TaskListProjection"/> that feeds the
/// <see cref="TableView"/> in the tasks browser's list pane.
///
/// <para>No I/O, no driver dependency — unit-testable without an application. Column order: Status,
/// Group, Id, Description. The Group column preserves the Active/Recent grouping that the previous
/// text renderer rendered as section headers.</para>
/// </summary>
internal sealed class TaskTableSource : ITableSource
{
    private static readonly string[] ColumnNamesArray = ["Status", "Group", "Id", "Description"];

    private readonly IReadOnlyList<(TaskListRow Row, string Group)> rows;
    private readonly StatusGlyphs glyphs;

    public TaskTableSource(TaskListProjection projection, StatusGlyphs glyphs)
    {
        this.glyphs = glyphs ?? StatusGlyphs.Unicode;
        this.rows = BuildRows(projection ?? TaskListProjection.Empty);
    }

    /// <inheritdoc/>
    public int Columns => ColumnNamesArray.Length;

    /// <inheritdoc/>
    public int Rows => this.rows.Count;

    /// <inheritdoc/>
    public string[] ColumnNames => ColumnNamesArray;

    /// <inheritdoc/>
    public object this[int row, int col]
    {
        get
        {
            var (listRow, group) = this.rows[row];
            var task = listRow.Task;
            return col switch
            {
                0 => this.glyphs[GetState(task)],
                1 => group,
                2 => TerminalTextSanitizer.SanitizeSingleLine(task.Id),
                3 => new string(' ', listRow.IndentDepth * 2) + TerminalTextSanitizer.SanitizeSingleLine(task.Description),
                _ => string.Empty,
            };
        }
    }

    /// <summary>
    /// Maps a <see cref="TaskSnapshot"/> to a <see cref="BrowserItemState"/>.
    ///
    /// <para>Mapping: Running → Healthy; Completed → Healthy; Failed → Error;
    /// Stopped → Idle.</para>
    /// </summary>
    public static BrowserItemState GetState(TaskSnapshot task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Status switch
        {
            TaskRunStatus.Running => BrowserItemState.Healthy,
            TaskRunStatus.Completed => BrowserItemState.Healthy,
            TaskRunStatus.Failed => BrowserItemState.Error,
            _ => BrowserItemState.Idle,
        };
    }

    /// <summary>Returns the <see cref="TaskListRow"/> at <paramref name="rowIndex"/>.</summary>
    public TaskListRow RowAt(int rowIndex) => this.rows[rowIndex].Row;

    private static IReadOnlyList<(TaskListRow, string)> BuildRows(TaskListProjection projection)
    {
        var result = new List<(TaskListRow, string)>(projection.Active.Count + projection.Recent.Count);
        foreach (var row in projection.Active)
        {
            result.Add((row, "active"));
        }

        foreach (var row in projection.Recent)
        {
            result.Add((row, "recent"));
        }

        return result;
    }
}
