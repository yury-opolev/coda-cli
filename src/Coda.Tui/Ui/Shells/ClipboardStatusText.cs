using System.Globalization;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// Builds the transient operational-row confirmations for clipboard copy (and, from Task 5, paste)
/// gestures and counts the copied symbols. A symbol is a Unicode grapheme/text element — combining
/// sequences and emoji count as one each — and the CR/LF row separators introduced by a multi-row
/// selection are excluded so the count reflects visible glyphs rather than line breaks.
/// </summary>
internal static class ClipboardStatusText
{
    /// <summary>
    /// Counts the Unicode grapheme/text elements in <paramref name="text"/>, treating combining sequences
    /// and emoji as a single symbol and skipping the CR, LF, and CRLF separators a multi-row selection adds.
    /// </summary>
    internal static int CountSymbols(string text)
    {
        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            if (element is "\r" or "\n" or "\r\n")
            {
                continue;
            }

            count++;
        }

        return count;
    }

    /// <summary>
    /// The transient confirmation for a successful copy, counting the copied symbols via
    /// <see cref="CountSymbols"/>. Singular for a single symbol, plural otherwise.
    /// </summary>
    internal static string Copied(string text)
    {
        var count = CountSymbols(text);
        return count == 1
            ? "1 symbol copied to clipboard"
            : $"{count} symbols copied to clipboard";
    }

    /// <summary>
    /// The transient confirmation for a successful paste, counting the pasted symbols via
    /// <see cref="CountSymbols"/>. Singular for a single symbol, plural otherwise. Reserved for Task 5.
    /// </summary>
    internal static string Pasted(string text)
    {
        var count = CountSymbols(text);
        return count == 1
            ? "1 symbol pasted from clipboard"
            : $"{count} symbols pasted from clipboard";
    }
}
