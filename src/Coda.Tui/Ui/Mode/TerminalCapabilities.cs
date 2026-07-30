namespace Coda.Tui.Ui.Mode;

/// <summary>Observed capabilities of the attached terminal.</summary>
/// <param name="UnicodeOutput">Whether the terminal can render box-drawing and geometric characters;
/// used to choose between the Unicode and ASCII glyph sets for the transcript gutter.</param>
public sealed record TerminalCapabilities(
    bool InputRedirected,
    bool OutputRedirected,
    int Width,
    int Height,
    bool Interactive,
    bool UnicodeOutput = true);

public interface ITerminalCapabilitiesProvider
{
    TerminalCapabilities Get();
}

public sealed class SystemTerminalCapabilitiesProvider : ITerminalCapabilitiesProvider
{
    public TerminalCapabilities Get()
    {
        try
        {
            var inputRedirected = Console.IsInputRedirected;
            var outputRedirected = Console.IsOutputRedirected;
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;

            var term = Environment.GetEnvironmentVariable("TERM");
            var interactive = !inputRedirected
                && !outputRedirected
                && !string.Equals(term, "dumb", StringComparison.Ordinal);

            var unicodeOutput = TerminalUnicodeDetection.Detect(
                OperatingSystem.IsWindows(),
                Console.OutputEncoding.CodePage,
                term,
                Environment.GetEnvironmentVariable("LANG"),
                Environment.GetEnvironmentVariable("LC_ALL"),
                Environment.GetEnvironmentVariable("LC_CTYPE"));

            return new TerminalCapabilities(
                inputRedirected,
                outputRedirected,
                width,
                height,
                interactive,
                unicodeOutput);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return new TerminalCapabilities(true, true, 0, 0, false, false);
        }
    }
}

/// <summary>Pure, testable Unicode capability detector that decides whether a terminal can render
/// box-drawing and geometric characters.</summary>
internal static class TerminalUnicodeDetection
{
    /// <summary>
    /// Detects whether the terminal supports Unicode output. Rules, in order:
    /// <list type="number">
    /// <item><description><paramref name="term"/> equal to <c>"dumb"</c> (ordinal, case-insensitive) ⇒ false.</description></item>
    /// <item><description>If <paramref name="isWindows"/> ⇒ true only when <paramref name="outputCodePage"/> is 65001, 1200, or 1201.</description></item>
    /// <item><description>Otherwise: if any of <paramref name="lcAll"/>, <paramref name="lcCtype"/>, <paramref name="lang"/> is non-empty,
    /// return true iff the first non-empty one (POSIX precedence: LC_ALL, LC_CTYPE, LANG) contains <c>"utf"</c> case-insensitively.</description></item>
    /// <item><description>If none of the three is set ⇒ true.</description></item>
    /// </list>
    /// </summary>
    internal static bool Detect(
        bool isWindows,
        int outputCodePage,
        string? term,
        string? lang,
        string? lcAll,
        string? lcCtype)
    {
        if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (isWindows)
        {
            return outputCodePage is 65001 or 1200 or 1201;
        }

        // POSIX locale precedence: LC_ALL overrides LC_CTYPE, which overrides LANG.
        var locale = !string.IsNullOrEmpty(lcAll) ? lcAll
            : !string.IsNullOrEmpty(lcCtype) ? lcCtype
            : !string.IsNullOrEmpty(lang) ? lang
            : null;

        if (locale is null)
        {
            return true;
        }

        return locale.Contains("utf", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record TuiModeDecision(TuiRunMode? Mode, string? Error);
