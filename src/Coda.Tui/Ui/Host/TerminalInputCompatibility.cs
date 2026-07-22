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
    public static string? SelectDriverName(Func<string, string?> getEnv, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(getEnv);

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
