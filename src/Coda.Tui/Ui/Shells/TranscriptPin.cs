using System.Text;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// Composes the one-line pin that keeps the prompt driving the current turn on screen after it has
/// scrolled out of the viewport. Pure text composition, kept separate from the view so the eliding and
/// visibility rules can be unit-tested without a Terminal.Gui host.
/// </summary>
/// <remarks>
/// The pin is what tells the user which prompt a buffered or placeholder turn belongs to, so it must NOT
/// be conditioned on streaming assistant text existing — only on <c>hasActiveWork</c>. This is important
/// for hooks-Phase-3 assistant-text buffering, where the assistant response may not yet be materialised
/// while the turn is still in progress.
/// </remarks>
internal static class TranscriptPin
{
    /// <summary>Builds the pin row for <paramref name="text"/> at <paramref name="width"/> cells, or null
    /// when there is nothing worth pinning.</summary>
    /// <remarks>
    /// Line selection happens AFTER sanitization, not before: a line that is non-blank to
    /// <see cref="string.Trim()"/> can still sanitize away to nothing (a pasted bare escape sequence, a lone
    /// bidi mark), and choosing it would blank the pin for the whole turn instead of falling through to the
    /// next real line. The scan also stops as soon as a second surviving line is found, since all it needs to
    /// know beyond the first is whether the prompt continues.
    /// </remarks>
    internal static string? Compose(string? text, int width, TranscriptGlyphs glyphs)
    {
        ArgumentNullException.ThrowIfNull(glyphs);

        var prefix = glyphs.Prefix(TranscriptGutterKind.UserMarker);
        var prefixWidth = TerminalCellText.Width(prefix);
        if (string.IsNullOrEmpty(text) || width < prefixWidth + 1)
        {
            return null;
        }

        var (content, hasMore) = FirstSurvivingLine(text);
        if (content is null)
        {
            return null;
        }

        var composed = prefix + content;
        if (!hasMore && TerminalCellText.Width(composed) <= width)
        {
            return composed;
        }

        // Elide: keep whole graphemes so a wide (CJK) character is never split, and reserve the ellipsis cell.
        var contentBudget = width - prefixWidth - 1;
        var truncated = new StringBuilder(prefix, capacity: width + 1);
        var usedCells = 0;
        foreach (var element in TerminalCellText.Enumerate(content))
        {
            var elementCells = Math.Max(1, element.CellWidth);
            if (usedCells + elementCells > contentBudget)
            {
                break;
            }

            truncated.Append(element.Text);
            usedCells += elementCells;
        }

        return truncated.Append('\u2026').ToString();
    }

    /// <summary>
    /// The first line of <paramref name="text"/> that still carries content once sanitized, plus whether any
    /// further such line follows. Scans without materializing the whole message: a submitted prompt can be a
    /// pasted log of arbitrary size, and this runs on the draw path.
    /// </summary>
    private static (string? Content, bool HasMore) FirstSurvivingLine(string text)
    {
        string? first = null;
        var index = 0;
        while (index < text.Length)
        {
            var breakIndex = text.IndexOfAny(LineBreaks, index);
            var end = breakIndex < 0 ? text.Length : breakIndex;
            var candidate = text.AsSpan(index, end - index).Trim();
            index = breakIndex < 0 ? text.Length : breakIndex + 1;

            if (candidate.IsEmpty)
            {
                continue;
            }

            var sanitized = TerminalTextSanitizer.SanitizeSingleLine(candidate.ToString());
            if (sanitized.Length == 0)
            {
                continue;
            }

            if (first is null)
            {
                first = sanitized;
                continue;
            }

            return (first, true);
        }

        return (first, false);
    }

    private static readonly char[] LineBreaks = ['\n', '\r'];

    /// <summary>Whether the pin should be drawn: output is being produced, a prompt exists, and none of
    /// that prompt's rows are currently visible in the viewport.</summary>
    internal static bool ShouldShow(
        bool hasActiveWork,
        int? blockFirstRow,
        int blockEndRowExclusive,
        int topRow,
        int viewportHeight)
    {
        if (!hasActiveWork || blockFirstRow is null || viewportHeight <= 0)
        {
            return false;
        }

        var blockStart = blockFirstRow.Value;
        var viewEnd = topRow + viewportHeight;

        // The block and the viewport do NOT intersect when the block is entirely above or entirely below.
        var blockAbove = blockEndRowExclusive <= topRow;
        var blockBelow = blockStart >= viewEnd;
        return blockAbove || blockBelow;
    }
}
