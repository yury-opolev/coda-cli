using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Markdig.Extensions.Tables;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// Lays a markdown table out for a fixed-width viewport: real columns, aligned, fitted to the space
/// available.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="TranscriptBlockFormatter"/> because the interesting part is arithmetic —
/// how wide each column may be when the natural widths do not fit — and that is worth testing without a
/// terminal or a markdown document. The formatter supplies cell text and takes back rows of strings.
/// <para>
/// All widths are terminal CELLS, so a table of CJK or emoji lines up as truthfully as one of ASCII.
/// </para>
/// </remarks>
internal static class MarkdownTableLayout
{
    /// <summary>The cells between two columns: a space, the rule, and a space.</summary>
    internal const int SeparatorCells = 3;

    /// <summary>Content cells a column keeps however tight the viewport gets.</summary>
    internal const int MinimumColumnWidth = 3;

    /// <summary>How a column's content sits within its width.</summary>
    internal enum ColumnAlignment
    {
        Left,
        Center,
        Right,
    }

    /// <summary>
    /// Allocates a width to each column so the total, including separators, fits
    /// <paramref name="available"/>.
    /// </summary>
    /// <remarks>
    /// Columns that already fit are left alone and only the greedy ones give ground: the widest column
    /// is trimmed repeatedly until the table fits. Narrow columns — a "Type" or a count — therefore keep
    /// their content while a long description absorbs the loss, which is the opposite of what
    /// proportional scaling would do.
    /// </remarks>
    public static int[] AllocateWidths(IReadOnlyList<int> naturalWidths, int available)
    {
        var count = naturalWidths.Count;
        var widths = new int[count];
        if (count == 0)
        {
            return widths;
        }

        for (var i = 0; i < count; i++)
        {
            widths[i] = Math.Max(1, naturalWidths[i]);
        }

        var separators = (count - 1) * SeparatorCells;
        var budget = Math.Max(count, available - separators);

        // Shave the widest column one cell at a time. Slower than solving it directly, but the loop is
        // bounded by the overflow and a table has few columns, and it keeps the rule obvious.
        var total = widths.Sum();
        while (total > budget)
        {
            var widest = 0;
            for (var i = 1; i < count; i++)
            {
                if (widths[i] > widths[widest])
                {
                    widest = i;
                }
            }

            if (widths[widest] <= MinimumColumnWidth)
            {
                break;  // Every column is at its floor; the caller clamps what is left.
            }

            widths[widest]--;
            total--;
        }

        return widths;
    }

    /// <summary>Pads <paramref name="text"/> to <paramref name="width"/> cells under an alignment.</summary>
    public static string Pad(string text, int width, ColumnAlignment alignment)
    {
        var used = TerminalCellText.Width(text);
        var slack = width - used;
        if (slack <= 0)
        {
            return text;
        }

        return alignment switch
        {
            ColumnAlignment.Right => new string(' ', slack) + text,
            ColumnAlignment.Center => new string(' ', slack / 2) + text + new string(' ', slack - (slack / 2)),
            _ => text + new string(' ', slack),
        };
    }

    /// <summary>Maps Markdig's per-column alignment onto ours, defaulting to left.</summary>
    public static ColumnAlignment From(TableColumnAlign? align) => align switch
    {
        TableColumnAlign.Right => ColumnAlignment.Right,
        TableColumnAlign.Center => ColumnAlignment.Center,
        _ => ColumnAlignment.Left,
    };

    /// <summary>Joins already-padded cells with the column rule.</summary>
    public static string JoinRow(IEnumerable<string> paddedCells) =>
        string.Join(" \u2502 ", paddedCells);

    /// <summary>The horizontal rule under the header, matching the column widths.</summary>
    public static string RuleRow(IReadOnlyList<int> widths) =>
        string.Join("\u2500\u253c\u2500", widths.Select(w => new string('\u2500', w)));

    /// <summary>
    /// Wraps <paramref name="text"/> to <paramref name="width"/> cells, returning at least one line.
    /// </summary>
    public static IReadOnlyList<string> WrapCell(string text, int width, Func<string, int, IEnumerable<string>> wrap)
    {
        if (text.Length == 0)
        {
            return [string.Empty];
        }

        var wrapped = wrap(text, Math.Max(1, width)).ToList();
        return wrapped.Count == 0 ? [string.Empty] : wrapped;
    }

    /// <summary>
    /// Builds the full set of rows for a table: header, rule, then one or more lines per body row.
    /// </summary>
    /// <param name="rows">Cell text per row; the first is the header when <paramref name="hasHeader"/>.</param>
    /// <param name="alignments">Alignment per column.</param>
    /// <param name="available">Cells the table may occupy.</param>
    /// <param name="wrap">The caller's cell-aware wrapper, so wrapping matches the rest of the transcript.</param>
    public static IReadOnlyList<TableRenderRow> Build(
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<ColumnAlignment> alignments,
        bool hasHeader,
        int available,
        Func<string, int, IEnumerable<string>> wrap)
    {
        var result = new List<TableRenderRow>();
        if (rows.Count == 0)
        {
            return result;
        }

        var columns = rows.Max(r => r.Count);
        if (columns == 0)
        {
            return result;
        }

        // Natural width: the widest cell in each column.
        var natural = new int[columns];
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Count; c++)
            {
                natural[c] = Math.Max(natural[c], TerminalCellText.Width(row[c]));
            }
        }

        var widths = AllocateWidths(natural, available);

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];

            // A ragged row is padded rather than rejected: a malformed table should still be readable.
            var cellLines = new List<string>[columns];
            var height = 1;
            for (var c = 0; c < columns; c++)
            {
                var text = c < row.Count ? row[c] : string.Empty;
                cellLines[c] = WrapCell(text, widths[c], wrap).ToList();
                height = Math.Max(height, cellLines[c].Count);
            }

            for (var line = 0; line < height; line++)
            {
                var padded = new string[columns];
                for (var c = 0; c < columns; c++)
                {
                    var text = line < cellLines[c].Count ? cellLines[c][line] : string.Empty;
                    padded[c] = Pad(text, widths[c], alignments.Count > c ? alignments[c] : ColumnAlignment.Left);
                }

                result.Add(new TableRenderRow(JoinRow(padded), r == 0 && hasHeader));
            }

            if (r == 0 && hasHeader)
            {
                result.Add(new TableRenderRow(RuleRow(widths), false, IsRule: true));
            }
        }

        return result;
    }
}

/// <summary>One laid-out table row, and what it is, so the formatter can colour it.</summary>
internal readonly record struct TableRenderRow(string Text, bool IsHeader, bool IsRule = false);
