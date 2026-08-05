using Coda.Agent.Scheduling;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Schedule;

/// <summary>
/// An <see cref="ITableSource"/> projection over a snapshot of <see cref="ScheduledTaskReadModel"/>s
/// that feeds the <see cref="TableView"/> in the schedule browser's list pane.
///
/// <para>No I/O, no driver dependency — unit-testable without an application. Column order: Status, Id,
/// Name, Rule, TimeZone, Next, Outcome.</para>
/// </summary>
internal sealed class ScheduleTableSource : ITableSource
{
    private static readonly string[] ColumnNamesArray = ["Status", "Id", "Name", "Rule", "TimeZone", "Next", "Outcome"];

    private readonly IReadOnlyList<ScheduledTaskReadModel> rows;
    private readonly StatusGlyphs glyphs;

    public ScheduleTableSource(IReadOnlyList<ScheduledTaskReadModel> rows, StatusGlyphs glyphs)
    {
        this.rows = rows ?? [];
        this.glyphs = glyphs ?? StatusGlyphs.Unicode;
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
            var item = this.rows[row];
            return col switch
            {
                0 => this.glyphs[GetState(item)],
                1 => TerminalTextSanitizer.SanitizeSingleLine(item.Id),
                2 => item.Name is { Length: > 0 } n ? TerminalTextSanitizer.SanitizeSingleLine(n) : string.Empty,
                3 => TerminalTextSanitizer.SanitizeSingleLine(item.Rule),
                4 => TerminalTextSanitizer.SanitizeSingleLine(item.TimeZone),
                5 => item.NextRunUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm"),
                6 => item.LastOutcome is { } lo ? TerminalTextSanitizer.SanitizeSingleLine(lo.Outcome.ToString()) : string.Empty,
                _ => string.Empty,
            };
        }
    }

    /// <summary>
    /// Maps a <see cref="ScheduledTaskReadModel"/> to a <see cref="BrowserItemState"/>.
    ///
    /// <para>Mapping: <see cref="ScheduleRuntimeStatus.Running"/> → <see cref="BrowserItemState.Healthy"/>;
    /// all other states → <see cref="BrowserItemState.Idle"/>.</para>
    /// </summary>
    public static BrowserItemState GetState(ScheduledTaskReadModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.State == ScheduleRuntimeStatus.Running ? BrowserItemState.Healthy : BrowserItemState.Idle;
    }

    /// <summary>Returns the <see cref="ScheduledTaskReadModel"/> at <paramref name="rowIndex"/>.</summary>
    public ScheduledTaskReadModel ItemAt(int rowIndex) => this.rows[rowIndex];
}
