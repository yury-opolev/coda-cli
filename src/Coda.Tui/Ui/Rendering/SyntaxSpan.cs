using System;
using System.Collections.Generic;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// A contiguous range of one rendered row painted in a syntax-highlight colour.
/// </summary>
/// <remarks>
/// Columns are terminal CELLS, not chars, and are local to the row they annotate — the same
/// convention <see cref="LinkSpan"/> uses, so both can feed the transcript's boundary-point draw
/// pass without conversion. A token whose text wraps contributes one span per render line.
/// </remarks>
/// <param name="StartColumn">Inclusive first cell.</param>
/// <param name="EndColumn">Exclusive last cell.</param>
/// <param name="Kind">The token kind, which selects the foreground colour.</param>
public readonly record struct SyntaxSpan(int StartColumn, int EndColumn, SyntaxTokenKind Kind);

/// <summary>
/// Projects tokenizer output — char offsets on a logical source line — onto the cell-based spans of
/// the render lines that line wrapped into.
/// </summary>
/// <remarks>
/// This is deliberately separate from both the tokenizer and the formatter: the tokenizer knows
/// languages but not layout, the formatter knows layout but not languages, and the arithmetic that
/// joins them (chars to cells, absolute to row-local, past a gutter) is the part worth testing on
/// its own.
/// </remarks>
internal static class SyntaxSpanMapper
{
    /// <summary>
    /// Clips <paramref name="charSpans"/> to one wrapped segment and rebases them onto that segment's
    /// own columns.
    /// </summary>
    /// <param name="segmentText">The wrapped segment as it will be drawn.</param>
    /// <param name="segmentCharStart">Where the segment begins in the logical line, in chars.</param>
    /// <param name="charSpans">Spans for the whole logical line, in logical char offsets.</param>
    /// <param name="cellOffset">
    /// Cells the row already spends before <paramref name="segmentText"/> begins — an indent, or a
    /// diff's line-number gutter. Every emitted column is shifted by this.
    /// </param>
    /// <returns>
    /// The spans overlapping this segment, ascending and non-overlapping, or <see langword="null"/>
    /// when none do. Null rather than an empty list so unhighlighted rows stay allocation-free.
    /// </returns>
    public static IReadOnlyList<SyntaxSpan>? MapSegment(
        string segmentText,
        int segmentCharStart,
        IReadOnlyList<SyntaxCharSpan> charSpans,
        int cellOffset)
    {
        if (charSpans.Count == 0 || segmentText.Length == 0)
        {
            return null;
        }

        var segmentCharEnd = segmentCharStart + segmentText.Length;
        List<SyntaxSpan>? mapped = null;

        foreach (var span in charSpans)
        {
            // Clip to the segment; skip spans that ended before it or begin after it.
            var start = Math.Max(span.StartChar, segmentCharStart);
            var end = Math.Min(span.EndChar, segmentCharEnd);
            if (start >= end)
            {
                continue;
            }

            // Measuring the prefix of the segment converts chars to cells, so wide glyphs earlier in
            // the row push the span right by the number of columns they actually occupy.
            var startCell = TerminalCellText.Width(segmentText[..(start - segmentCharStart)]) + cellOffset;
            var endCell = TerminalCellText.Width(segmentText[..(end - segmentCharStart)]) + cellOffset;
            if (startCell >= endCell)
            {
                continue;
            }

            (mapped ??= []).Add(new SyntaxSpan(startCell, endCell, span.Kind));
        }

        return mapped;
    }
}
