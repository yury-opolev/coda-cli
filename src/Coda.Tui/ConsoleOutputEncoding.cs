using System.Text;

namespace Coda.Tui;

/// <summary>
/// One-time console output configuration run at the very first line of the process, before any
/// component captures <see cref="Console.Out"/> or the ambient Spectre console.
/// </summary>
/// <remarks>
/// On Windows the console starts on an OEM codepage (typically CP437). Terminal.Gui's ANSI output
/// later flips the codepage to UTF-8, but by then Spectre's <c>AnsiConsole.Console</c> — and the exit
/// summary renderer that reuses it — have already captured a <see cref="Console.Out"/> writer bound to
/// the stale CP437 encoding. That stale writer then emits invalid bytes (mojibake) for box-drawing
/// glyphs and the middle-dot separator into a terminal that is now interpreting bytes as UTF-8.
/// Forcing UTF-8 here, before any capture, guarantees every downstream writer speaks UTF-8 from the
/// start. UTF-8 is used without a BOM so no stray preamble bytes are written to stdout. Only output is
/// configured; input encoding is left untouched because nothing in the startup path depends on it.
/// </remarks>
internal static class ConsoleOutputEncoding
{
    /// <summary>
    /// Force console output onto UTF-8 (no BOM). In production the encoding is assigned to
    /// <see cref="Console.OutputEncoding"/>, which rebuilds <see cref="Console.Out"/> with a writer bound
    /// to UTF-8 so any later capture is UTF-8-correct. Best-effort: if the process has no real console
    /// (e.g. fully redirected stdout on some hosts) the setter can throw, and a startup encoding tweak
    /// must never abort the program.
    /// </summary>
    /// <param name="setEncoding">
    /// Seam for tests to observe the configured encoding without mutating global console state. When
    /// <c>null</c> (the production default) the encoding is assigned to <see cref="Console.OutputEncoding"/>.
    /// </param>
    public static void ConfigureUtf8(Action<Encoding>? setEncoding = null)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var apply = setEncoding ?? (encoding => Console.OutputEncoding = encoding);
        try
        {
            apply(utf8);
        }
        catch (IOException)
        {
            // No console handle to reconfigure (redirected/detached stdout): nothing to fix here.
        }
    }
}
