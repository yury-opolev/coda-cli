using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// Renders raw task output safe for terminal display. <see cref="Sanitize"/> strips ANSI/OSC escape
/// sequences and control characters so a task can never move the cursor, clear the screen, set the
/// window title, emit a hyperlink, or otherwise spoof the terminal; printable Unicode (box drawing,
/// accented text, emoji) is preserved, tab and newline are kept, and carriage returns (including bare
/// CR "progress" rewinds) are normalized to newline. <see cref="SanitizeForMarkup"/> additionally
/// escapes Spectre.Console markup so bracketed text from a task can never be interpreted as markup.
/// <see cref="StripAnsiEscapes"/> is the shared escape-stripping primitive reused by the plain and
/// Spectre renderers so the escape grammar lives in exactly one place. It lives in the neutral
/// <c>Coda.Tui.Ui.Rendering</c> namespace because those generic renderers depend on it.
/// </summary>
internal static partial class TerminalTextSanitizer
{
    // CSI (ESC [ ... final byte), OSC (ESC ] ... terminated by BEL or ST), and simple two-character
    // escapes (ESC followed by @-Z, backslash, or underscore — covers ST, RIS, and Fe/Fs sequences).
    [GeneratedRegex(@"\x1B(?:[@-Z\\_]|\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))", RegexOptions.Compiled)]
    private static partial Regex AnsiEscape();

    /// <summary>
    /// Removes ANSI/OSC escape sequences only, leaving carriage returns, tabs, and other control
    /// characters untouched for the caller to handle. After the single regex pass, any residual bare
    /// ESC (U+001B) is dropped so that removing a matched sequence can never splice a leftover ESC onto
    /// the following literal text to reform a live escape sequence (e.g. <c>ESC ESC [2J [2J</c> or
    /// <c>ESC ESC A [2J</c>, where the inner sequence is removed and the surviving ESC would otherwise
    /// glue onto a trailing <c>[2J</c>). The result is guaranteed to contain no U+001B. This is O(n):
    /// the regex is backtracking-safe and the residual scan is a single linear pass. Callers that also
    /// need control-character and carriage-return handling use <see cref="Sanitize"/>.
    /// </summary>
    public static string StripAnsiEscapes(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var stripped = AnsiEscape().Replace(text, string.Empty);

        // Removing an ESC can never introduce a new ESC, so a single linear scan that drops every
        // remaining U+001B is sufficient to guarantee no live escape sequence survives or reforms.
        if (stripped.IndexOf('\x1B') < 0)
        {
            return stripped;
        }

        var sb = new StringBuilder(stripped.Length);
        foreach (var ch in stripped)
        {
            if (ch != '\x1B')
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>Strips escapes and unsafe control characters, normalizing CR/CRLF to LF.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var stripped = StripAnsiEscapes(text);

        var sb = new StringBuilder(stripped.Length);
        for (var i = 0; i < stripped.Length; i++)
        {
            var ch = stripped[i];
            if (ch == '\r')
            {
                // Normalize CRLF and bare CR (carriage-return progress) to a single LF so a task can
                // neither rewind the cursor to column 0 nor overwrite previously rendered content.
                if (i + 1 < stripped.Length && stripped[i + 1] == '\n')
                {
                    i++;
                }

                sb.Append('\n');
                continue;
            }

            if (ch == '\n' || ch == '\t')
            {
                sb.Append(ch);
                continue;
            }

            if (char.IsControl(ch))
            {
                continue; // drop every other C0/C1/DEL control (ESC, BEL, BS, NUL, 0x7F-0x9F, ...)
            }

            if (IsBidiFormattingControl(ch))
            {
                // Drop Unicode bidirectional overrides/isolates and directional marks. These are
                // invisible formatting controls that can visually reorder text to spoof output (for
                // example rendering a dangerous command as a benign one). Emoji ZWJ sequences
                // (U+200D) and all other printable Unicode are intentionally preserved.
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>Sanitizes then escapes Spectre.Console markup (for the plain/Spectre <c>/tasks</c> snapshot).</summary>
    public static string SanitizeForMarkup(string? text) => Markup.Escape(Sanitize(text));

    // Unicode bidirectional formatting controls (category Cf) that can visually reorder rendered text:
    // the embeddings/overrides U+202A-202E, the isolates U+2066-2069, and the directional marks
    // LRM (U+200E), RLM (U+200F), and ALM (U+061C ARABIC LETTER MARK). U+200D (ZERO WIDTH JOINER) is
    // deliberately excluded so emoji ZWJ sequences keep rendering correctly.
    private static bool IsBidiFormattingControl(char ch) => ch is
        '\u061C' or
        '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or
        '\u2066' or '\u2067' or '\u2068' or '\u2069' or
        '\u200E' or '\u200F';
}
