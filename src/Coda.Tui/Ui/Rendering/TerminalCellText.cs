using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// A single grapheme cluster projected onto the terminal grid: its text, its position in the UTF-16
/// source string, and the horizontal cell it starts at together with how many display cells it occupies.
/// </summary>
/// <param name="Text">The grapheme cluster (a Unicode text element) as it appears in the source.</param>
/// <param name="SourceIndex">The UTF-16 index in the source string where the grapheme begins.</param>
/// <param name="SourceLength">The number of UTF-16 code units the grapheme spans in the source.</param>
/// <param name="CellStart">The zero-based terminal cell the grapheme starts at.</param>
/// <param name="Width">The number of display cells the grapheme occupies (0, 1, or 2).</param>
internal readonly record struct TerminalTextElement(
    string Text, int SourceIndex, int SourceLength, int CellStart, int Width);

/// <summary>
/// A wrapped row produced by <see cref="TerminalCellText.Wrap"/>: the row text alongside the half-open
/// UTF-16 source range it covers and its total display-cell width.
/// </summary>
/// <param name="Text">The visible text of the row (boundary whitespace between rows is dropped).</param>
/// <param name="SourceStart">The inclusive UTF-16 index in the source where the row begins.</param>
/// <param name="SourceEnd">The exclusive UTF-16 index in the source where the row ends.</param>
/// <param name="Width">The total display-cell width of <see cref="Text"/>.</param>
internal readonly record struct WrappedCellRow(string Text, int SourceStart, int SourceEnd, int Width);

/// <summary>
/// Shared terminal cell-layout primitives used by the transcript renderer and shells. Measures display
/// width by Unicode rune (combining/format marks are zero width, wide East Asian and emoji runes are two
/// cells, everything else is one), enumerates grapheme clusters with both their UTF-16 source indices and
/// their terminal cell offsets, slices by a cell range while keeping every whole grapheme that intersects
/// it, and word-wraps by display cells without ever splitting a grapheme cluster.
/// </summary>
internal static class TerminalCellText
{
    /// <summary>Total display-cell width of <paramref name="text"/>.</summary>
    internal static int Width(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += RuneWidth(rune);
        }

        return width;
    }

    /// <summary>
    /// Enumerates the grapheme clusters of <paramref name="text"/>, reporting each one's UTF-16 source
    /// index, source length, starting terminal cell, and display width.
    /// </summary>
    internal static IReadOnlyList<TerminalTextElement> Enumerate(string? text)
    {
        var elements = new List<TerminalTextElement>();
        if (string.IsNullOrEmpty(text))
        {
            return elements;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var cell = 0;
        while (enumerator.MoveNext())
        {
            var grapheme = (string)enumerator.Current;
            var width = Width(grapheme);
            elements.Add(new TerminalTextElement(grapheme, enumerator.ElementIndex, grapheme.Length, cell, width));
            cell += width;
        }

        return elements;
    }

    /// <summary>
    /// Returns the substring of <paramref name="text"/> covering the half-open cell range
    /// <paramref name="startCell"/>..<paramref name="endCell"/>, including every whole grapheme cluster
    /// that intersects the range (a wide grapheme straddling the boundary is included in full).
    /// </summary>
    internal static string SliceByCells(string? text, int startCell, int endCell)
    {
        if (string.IsNullOrEmpty(text) || endCell <= startCell)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var element in Enumerate(text))
        {
            var elementEnd = element.CellStart + element.Width;
            if (element.CellStart < endCell && elementEnd > startCell)
            {
                builder.Append(element.Text);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Word-wraps <paramref name="text"/> to <paramref name="width"/> display cells. Newlines are
    /// preserved as row breaks (a trailing or empty line yields an empty row), breaks prefer whitespace,
    /// runs longer than the width are hard-broken at grapheme boundaries, and no grapheme is ever split.
    /// </summary>
    internal static IReadOnlyList<WrappedCellRow> Wrap(string? text, int width)
    {
        var cellWidth = width > 0 ? width : 1;
        var rows = new List<WrappedCellRow>();
        var source = text ?? string.Empty;

        var lineStart = 0;
        while (true)
        {
            var newline = source.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? source.Length : newline;
            WrapLogicalLine(rows, source, lineStart, lineEnd, cellWidth);

            if (newline < 0)
            {
                break;
            }

            lineStart = newline + 1;
        }

        return rows;
    }

    private static void WrapLogicalLine(List<WrappedCellRow> rows, string source, int lineStart, int lineEnd, int width)
    {
        if (lineEnd <= lineStart)
        {
            rows.Add(new WrappedCellRow(string.Empty, lineStart, lineStart, 0));
            return;
        }

        var emittedBefore = rows.Count;
        int rowStart = -1, rowEnd = -1, rowWidth = 0;

        void EmitRow(int start, int end)
        {
            var chunk = source[start..end];
            rows.Add(new WrappedCellRow(chunk, start, end, Width(chunk)));
        }

        void PlaceWord(int wordStart, int wordEnd, int wordWidth)
        {
            if (wordWidth <= width)
            {
                rowStart = wordStart;
                rowEnd = wordEnd;
                rowWidth = wordWidth;
                return;
            }

            // Word is wider than the line: hard-break it at grapheme boundaries, keeping the tail as the
            // in-progress row.
            var chunks = ChunkWord(source, wordStart, wordEnd, width);
            for (var i = 0; i < chunks.Count; i++)
            {
                var (chunkStart, chunkEnd, chunkWidth) = chunks[i];
                if (i == chunks.Count - 1)
                {
                    rowStart = chunkStart;
                    rowEnd = chunkEnd;
                    rowWidth = chunkWidth;
                }
                else
                {
                    EmitRow(chunkStart, chunkEnd);
                }
            }
        }

        var pos = lineStart;
        while (pos < lineEnd)
        {
            if (source[pos] == ' ')
            {
                pos++;
                continue;
            }

            var wordStart = pos;
            while (pos < lineEnd && source[pos] != ' ')
            {
                pos++;
            }

            var wordEnd = pos;
            var wordWidth = Width(source[wordStart..wordEnd]);

            if (rowStart < 0)
            {
                PlaceWord(wordStart, wordEnd, wordWidth);
                continue;
            }

            var gap = wordStart - rowEnd; // spaces between the previous word and this one, each one cell.
            if (rowWidth + gap + wordWidth <= width)
            {
                rowEnd = wordEnd;
                rowWidth += gap + wordWidth;
                continue;
            }

            EmitRow(rowStart, rowEnd);
            rowStart = -1;
            rowWidth = 0;
            PlaceWord(wordStart, wordEnd, wordWidth);
        }

        if (rowStart >= 0)
        {
            EmitRow(rowStart, rowEnd);
        }

        if (rows.Count == emittedBefore)
        {
            // Whitespace-only line: still occupies a row.
            rows.Add(new WrappedCellRow(string.Empty, lineStart, lineStart, 0));
        }
    }

    private static List<(int Start, int End, int Width)> ChunkWord(string source, int wordStart, int wordEnd, int width)
    {
        var chunks = new List<(int Start, int End, int Width)>();
        var chunkStart = wordStart;
        var chunkEnd = wordStart;
        var chunkWidth = 0;

        foreach (var element in Enumerate(source[wordStart..wordEnd]))
        {
            var elementStart = wordStart + element.SourceIndex;
            var elementEnd = elementStart + element.SourceLength;

            if (chunkWidth > 0 && chunkWidth + element.Width > width)
            {
                chunks.Add((chunkStart, chunkEnd, chunkWidth));
                chunkStart = elementStart;
                chunkWidth = 0;
            }

            chunkEnd = elementEnd;
            chunkWidth += element.Width;
        }

        chunks.Add((chunkStart, chunkEnd, chunkWidth));
        return chunks;
    }

    private static int RuneWidth(System.Text.Rune rune)
    {
        var value = rune.Value;
        if (value == 0)
        {
            return 0;
        }

        var category = System.Text.Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        return IsWide(value) ? 2 : 1;
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
        (codePoint >= 0x1F300 && codePoint <= 0x1FAFF) || // Emoji / symbols
        (codePoint >= 0x20000 && codePoint <= 0x3FFFD);   // CJK Ext B+
}
