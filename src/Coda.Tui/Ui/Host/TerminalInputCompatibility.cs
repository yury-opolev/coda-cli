using Terminal.Gui;

namespace Coda.Tui.Ui.Host;

/// <summary>
/// Selects the Terminal.Gui input driver needed for Windows Terminal's Kitty keyboard protocol and
/// provides a pure seam for normalizing modified Enter keys. Other hosts retain Terminal.Gui's
/// platform-default driver.
/// </summary>
internal static class TerminalInputCompatibility
{
    /// <summary>
    /// Returns the driver name to pass to <c>IApplication.Init</c>, or <see langword="null"/> for
    /// Terminal.Gui's platform default.
    /// </summary>
    /// <remarks>
    /// The <c>CODA_TUI_DRIVER</c> environment variable overrides selection for troubleshooting input
    /// latency. The Terminal.Gui v2 <c>ansi</c> driver (chosen for Windows Terminal so modified keys like
    /// Shift+Enter work) polls stdin on a background thread, which can batch fast typing into visible bursts;
    /// setting <c>CODA_TUI_DRIVER=windows</c> selects the event-driven native Windows driver instead (at the
    /// cost of the Kitty-keyboard features the ansi driver provides). Recognized values are the Terminal.Gui
    /// driver names (<c>windows</c>, <c>dotnet</c>, <c>ansi</c>); <c>default</c> forces the platform default.
    /// </remarks>
    public static string? SelectDriverName(Func<string, string?> getEnv, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(getEnv);

        var overrideName = getEnv("CODA_TUI_DRIVER");
        if (!string.IsNullOrWhiteSpace(overrideName))
        {
            var trimmed = overrideName.Trim();
            return trimmed.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
        }

        var wtSession = getEnv("WT_SESSION");
        return isWindows && !string.IsNullOrWhiteSpace(wtSession)
            ? DriverRegistry.Names.ANSI
            : null;
    }

    /// <summary>Uses the current process environment and operating system.</summary>
    public static string? SelectDriverName()
        => SelectDriverName(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());

    /// <summary>
    /// Keeps native modified Enter unchanged and passes unknown keys through for future driver-specific
    /// normalization without changing existing input behavior.
    /// </summary>
    public static Key NormalizeModifiedEnter(Key key)
        => key == Key.Enter.WithShift ? Key.Enter.WithShift : key;
}
