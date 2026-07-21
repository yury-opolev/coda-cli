using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Coda.Tui.Ui.Tasks;

/// <summary>
/// Renders raw task output safe for terminal display. <see cref="Sanitize"/> strips ANSI/OSC escape
/// sequences and control characters so a task can never move the cursor, clear the screen, set the
/// window title, emit a hyperlink, or otherwise spoof the terminal; printable Unicode (box drawing,
/// accented text, emoji) is preserved, tab and newline are kept, and carriage returns (including bare
/// CR "progress" rewinds) are normalized to newline. <see cref="SanitizeForMarkup"/> additionally
/// escapes Spectre.Console markup so bracketed text from a task can never be interpreted as markup.
/// <see cref="StripAnsiEscapes"/> is the shared escape-stripping primitive reused by the plain and
/// Spectre renderers so the escape grammar lives in exactly one place.
/// </summary>
internal static partial class TaskTextSanitizer
{
    // CSI (ESC [ ... final byte), OSC (ESC ] ... terminated by BEL or ST), and simple two-character
    // escapes (ESC followed by @-Z, backslash, or underscore — covers ST, RIS, and Fe/Fs sequences).
    [GeneratedRegex(@"\x1B(?:[@-Z\\_]|\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))", RegexOptions.Compiled)]
    private static partial Regex AnsiEscape();

    /// <summary>
    /// Removes ANSI/OSC escape sequences only, leaving all other characters (including control
    /// characters and carriage returns) untouched. Callers that also need control-character handling
    /// use <see cref="Sanitize"/>.
    /// </summary>
    public static string StripAnsiEscapes(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : AnsiEscape().Replace(text, string.Empty);

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

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>Sanitizes then escapes Spectre.Console markup (for the plain/Spectre <c>/tasks</c> snapshot).</summary>
    public static string SanitizeForMarkup(string? text) => Markup.Escape(Sanitize(text));
}
