using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// A single grapheme cluster projected onto the terminal grid: its text, its position in the UTF-16
/// source string, and the horizontal cell it starts at together with how many display cells it occupies.
/// </summary>
/// <param name="Text">The grapheme cluster (a Unicode text element) as it appears in the source.</param>
/// <param name="Utf16Start">The UTF-16 index in the source string where the grapheme begins.</param>
/// <param name="Utf16Length">The number of UTF-16 code units the grapheme spans in the source.</param>
/// <param name="CellStart">The zero-based terminal cell the grapheme starts at.</param>
/// <param name="CellWidth">The number of display cells the grapheme occupies (0, 1, or 2).</param>
internal readonly record struct TerminalTextElement(
    string Text,
    int Utf16Start,
    int Utf16Length,
    int CellStart,
    int CellWidth);

/// <summary>
/// A wrapped row produced by <see cref="TerminalCellText.Wrap"/>: the row text, the half-open UTF-16
/// source range it covers, its total display-cell width, and the grapheme clusters it contains. Each
/// element's <see cref="TerminalTextElement.Utf16Start"/> is an absolute index into the original source
/// string while its <see cref="TerminalTextElement.CellStart"/> is rebased so the first element of the
/// row starts at cell zero, which downstream selection/caret math relies on.
/// </summary>
/// <param name="Text">The visible text of the row (boundary whitespace between rows is dropped).</param>
/// <param name="StartIndex">The inclusive UTF-16 index in the source where the row begins.</param>
/// <param name="EndIndex">The exclusive UTF-16 index in the source where the row ends.</param>
/// <param name="CellWidth">The total display-cell width of <see cref="Text"/>.</param>
/// <param name="Elements">The grapheme clusters of the row with absolute UTF-16 indices and rebased cell starts.</param>
internal readonly record struct WrappedCellRow(
    string Text,
    int StartIndex,
    int EndIndex,
    int CellWidth,
    ImmutableArray<TerminalTextElement> Elements);

/// <summary>
/// Shared terminal cell-layout primitives used by the transcript renderer and shells. Measures display
/// width per grapheme cluster (combining/format marks add nothing and the cluster takes the maximum of
/// its runes' widths rather than the sum, so multi-rune emoji and ZWJ sequences count as one wide glyph),
/// enumerates grapheme clusters with both their UTF-16 source indices and their terminal cell offsets,
/// slices by a cell range while keeping every whole grapheme that intersects it, and word-wraps by
/// display cells without ever splitting a grapheme cluster.
/// </summary>
internal static class TerminalCellText
{
    /// <summary>Total display-cell width of <paramref name="text"/>.</summary>
    public static int Width(string? text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text ?? string.Empty);
        var total = 0;
        while (enumerator.MoveNext())
        {
            total += ElementWidth((string)enumerator.Current);
        }

        return total;
    }

    /// <summary>
    /// Enumerates the grapheme clusters of <paramref name="text"/>, reporting each one's UTF-16 source
    /// index, source length, starting terminal cell, and display width.
    /// </summary>
    public static ImmutableArray<TerminalTextElement> Enumerate(string? text)
    {
        text ??= string.Empty;
        var builder = ImmutableArray.CreateBuilder<TerminalTextElement>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var cell = 0;
        while (enumerator.MoveNext())
        {
            var value = (string)enumerator.Current;
            var width = ElementWidth(value);
            builder.Add(new TerminalTextElement(value, enumerator.ElementIndex, value.Length, cell, width));
            cell += width;
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Returns the substring of <paramref name="text"/> covering the half-open cell range
    /// <paramref name="startCell"/>..<paramref name="endCellExclusive"/>, including every whole grapheme
    /// cluster that intersects the range (a wide grapheme straddling the boundary is included in full and
    /// a zero-width grapheme is treated as occupying one cell so it still intersects).
    /// </summary>
    public static string SliceByCells(string? text, int startCell, int endCellExclusive)
    {
        if (string.IsNullOrEmpty(text) || endCellExclusive <= startCell)
        {
            return string.Empty;
        }

        var start = Math.Max(0, startCell);
        var end = Math.Max(start, endCellExclusive);
        var builder = new StringBuilder();
        foreach (var element in Enumerate(text))
        {
            var elementEnd = element.CellStart + Math.Max(1, element.CellWidth);
            if (elementEnd <= start || element.CellStart >= end)
            {
                continue;
            }

            builder.Append(element.Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Word-wraps <paramref name="text"/> to <paramref name="width"/> display cells. Newlines are
    /// preserved as row breaks (a trailing or empty line yields an empty row), breaks prefer whitespace,
    /// runs longer than the width are hard-broken at grapheme boundaries, and no grapheme is ever split.
    /// </summary>
    public static ImmutableArray<WrappedCellRow> Wrap(string? text, int width)
    {
        text ??= string.Empty;
        var safeWidth = Math.Max(1, width);
        var rows = ImmutableArray.CreateBuilder<WrappedCellRow>();
        var logicalStart = 0;

        while (logicalStart <= text.Length)
        {
            var newline = text.IndexOf('\n', logicalStart);
            var logicalEnd = newline < 0 ? text.Length : newline;
            AppendLogicalLine(rows, text, logicalStart, logicalEnd, safeWidth);
            if (newline < 0)
            {
                break;
            }

            logicalStart = newline + 1;
            if (logicalStart == text.Length)
            {
                rows.Add(new WrappedCellRow(string.Empty, logicalStart, logicalStart, 0, []));
                break;
            }
        }

        return rows.Count == 0
            ? [new WrappedCellRow(string.Empty, 0, 0, 0, [])]
            : rows.ToImmutable();
    }

    private static void AppendLogicalLine(
        ImmutableArray<WrappedCellRow>.Builder rows,
        string source,
        int start,
        int end,
        int width)
    {
        if (start == end)
        {
            rows.Add(new WrappedCellRow(string.Empty, start, end, 0, []));
            return;
        }

        var line = source[start..end];
        var elements = Enumerate(line);
        var rowStart = 0;
        var appended = false;
        while (rowStart < elements.Length)
        {
            // Skip leading/boundary/trailing whitespace so a run between wrap points is never emitted as
            // its own row; a non-whitespace grapheme (even one wider than the width) still hard-emits below.
            while (rowStart < elements.Length && string.IsNullOrWhiteSpace(elements[rowStart].Text))
            {
                rowStart++;
            }

            if (rowStart >= elements.Length)
            {
                break;
            }

            var used = 0;
            var cursor = rowStart;
            var lastWhitespace = -1;
            while (cursor < elements.Length)
            {
                var next = elements[cursor];
                var nextWidth = Math.Max(1, next.CellWidth);
                if (used > 0 && used + nextWidth > width)
                {
                    break;
                }

                used += nextWidth;
                if (string.IsNullOrWhiteSpace(next.Text))
                {
                    lastWhitespace = cursor;
                }

                cursor++;
                if (used >= width)
                {
                    break;
                }
            }

            var rowEnd = cursor;
            if (cursor < elements.Length && lastWhitespace >= rowStart)
            {
                rowEnd = lastWhitespace;
                cursor = lastWhitespace + 1;
            }

            if (rowEnd <= rowStart)
            {
                rowEnd = Math.Min(elements.Length, rowStart + 1);
                cursor = rowEnd;
            }

            var rowElements = elements[rowStart..rowEnd];
            var rowText = string.Concat(rowElements.Select(element => element.Text));
            var absoluteStart = start + rowElements[0].Utf16Start;
            var last = rowElements[^1];
            var absoluteEnd = start + last.Utf16Start + last.Utf16Length;
            var baseCell = rowElements[0].CellStart;
            var normalized = rowElements
                .Select(element => element with
                {
                    Utf16Start = start + element.Utf16Start,
                    CellStart = element.CellStart - baseCell,
                })
                .ToImmutableArray();
            rows.Add(new WrappedCellRow(rowText, absoluteStart, absoluteEnd, Width(rowText), normalized));
            rowStart = cursor;
            appended = true;
        }

        if (!appended)
        {
            // The logical line was non-empty but all whitespace: preserve the newline structure with an
            // empty row rather than emitting the whitespace as visible content.
            rows.Add(new WrappedCellRow(string.Empty, start, end, 0, []));
        }
    }

    /// <summary>
    /// Display width of a single grapheme cluster: combining and format runes contribute nothing, and the
    /// cluster's width is the maximum of its runes' widths (wide runes are two cells, others one) rather
    /// than the sum, so a multi-rune emoji or ZWJ sequence occupies a single wide glyph.
    /// </summary>
    private static int ElementWidth(string element)
    {
        var width = 0;
        foreach (var rune in element.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            {
                continue;
            }

            width = Math.Max(width, IsWide(rune.Value) ? 2 : 1);
        }

        return width;
    }

    private static bool IsWide(int codePoint) =>
        (codePoint >= 0x1100 && codePoint <= 0x115F) ||   // Hangul Jamo
        (codePoint >= 0x2E80 && codePoint <= 0x303E) ||   // CJK radicals, Kangxi
        (codePoint >= 0x3041 && codePoint <= 0x33FF) ||   // Hiragana … CJK symbols
        (codePoint >= 0x3400 && codePoint <= 0x4DBF) ||   // CJK Ext A
        (codePoint >= 0x4E00 && codePoint <= 0x9FFF) ||   // CJK Unified
        (codePoint >= 0xA000 && codePoint <= 0xA4CF) ||   // Yi
        (codePoint >= 0xAC00 && codePoint <= 0xD7A3) ||   // Hangul syllables
        (codePoint >= 0xF900 && codePoint <= 0xFAFF) ||   // CJK compatibility
        (codePoint >= 0xFF00 && codePoint <= 0xFF60) ||   // Fullwidth forms
        (codePoint >= 0xFFE0 && codePoint <= 0xFFE6) ||   // Fullwidth signs
        (codePoint >= 0x1F1E6 && codePoint <= 0x1F1FF) || // Regional indicators (flags)
        (codePoint >= 0x1F300 && codePoint <= 0x1FAFF) || // Emoji / symbols
        (codePoint >= 0x20000 && codePoint <= 0x3FFFD);   // CJK Ext B+
}
