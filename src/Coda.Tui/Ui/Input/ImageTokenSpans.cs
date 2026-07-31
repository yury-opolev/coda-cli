using System.Text.RegularExpressions;

namespace Coda.Tui.Ui.Input;

/// <summary>
/// Locates the <c>[Image N]</c> tokens in a draft so the composer can treat each one as a single thing.
/// </summary>
/// <remarks>
/// A token is the draft's reference to a staged image: <c>AgentRunner.BuildImageTurnContent</c> attaches an
/// image only while its whole token survives. Deleting into one a character at a time therefore both leaves
/// visible wreckage in the prompt and silently drops the attachment, with nothing on screen to explain why.
/// Removing the token whole keeps the two in step — what you see in the draft is what gets sent.
/// <para>
/// Pure and host-neutral so the rule can be tested without a terminal. The pattern deliberately matches
/// <c>AgentRunner</c>'s exactly; if one changes the other must too, or the composer would protect a token
/// shape the agent does not honour.
/// </para>
/// </remarks>
internal static class ImageTokenSpans
{
    private static readonly Regex TokenPattern = new(
        @"\[Image (\d+)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The span a delete at <paramref name="caret"/> should remove, when an ordinary single-character
    /// delete would break an <c>[Image N]</c> token apart.
    /// </summary>
    /// <param name="text">The draft.</param>
    /// <param name="caret">The caret index, in chars.</param>
    /// <param name="forward">
    /// <see langword="true"/> for Delete, which consumes the character at the caret;
    /// <see langword="false"/> for Backspace, which consumes the one behind it.
    /// </param>
    /// <returns>
    /// The token's <c>[start, end)</c> char range, or <see langword="null"/> when the delete touches no
    /// token and should proceed one character at a time.
    /// </returns>
    public static (int Start, int End)? DeleteSpanAt(string? text, int caret, bool forward)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // The index the keystroke would actually consume.
        var target = forward ? caret : caret - 1;
        if (target < 0 || target >= text.Length)
        {
            return null;
        }

        foreach (Match match in TokenPattern.Matches(text))
        {
            var start = match.Index;
            var end = match.Index + match.Length;
            if (target >= start && target < end)
            {
                return (start, end);
            }
        }

        return null;
    }
}
