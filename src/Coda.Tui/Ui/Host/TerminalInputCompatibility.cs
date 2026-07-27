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
    /// On Windows Terminal the ANSI driver is preferred over the platform default: it supports the
    /// Kitty keyboard protocol so Shift+Enter keeps working. When the resolved name is
    /// <c>"ansi"</c> — or when it is <see langword="null"/> and the platform default is ANSI —
    /// <see cref="ShouldUseDiffingOutput"/> returns <see langword="true"/> and the caller may
    /// activate the diffing output layer as a drop-in replacement at the application-creation
    /// level. Set <c>CODA_TUI_DRIVER=default</c> to force Terminal.Gui's platform default. Set
    /// <c>CODA_TUI_DIFF=0</c> (or <c>false</c>/<c>off</c>) to disable the diffing output without
    /// changing input driver selection. Any other <c>CODA_TUI_DRIVER</c> value overrides automatic
    /// selection.
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
    /// Returns <see langword="true"/> when the Coda diffing output layer should be activated for
    /// the resolved driver name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The diffing output layer is a transparent drop-in for the ANSI driver. When
    /// <paramref name="resolvedDriverName"/> is <see langword="null"/> or blank — meaning
    /// Terminal.Gui will choose its platform default — the actual default driver name is resolved
    /// via <paramref name="getDefaultDriverName"/> before the comparison. This ensures the diffing
    /// output activates on all platforms where Terminal.Gui defaults to the ANSI driver (Linux,
    /// macOS, Windows conhost, VS Code terminal, ConEmu, and Git Bash), not only when the driver
    /// was selected explicitly.
    /// </para>
    /// <para>
    /// Set the environment variable <c>CODA_TUI_DIFF</c> to <c>0</c>, <c>false</c>, or <c>off</c>
    /// (case-insensitive, leading/trailing whitespace trimmed) to disable the diffing output and
    /// use the stock driver instead. This escape hatch lets a user bisect rendering artefacts
    /// introduced by the diffing layer without losing the Kitty-keyboard input support that
    /// motivated choosing the ANSI driver on Windows Terminal — use
    /// <c>CODA_TUI_DRIVER=default</c> to also revert input driver selection if needed.
    /// </para>
    /// </remarks>
    internal static bool ShouldUseDiffingOutput(
        string? resolvedDriverName,
        Func<string, string?>? getEnv = null,
        Func<string>? getDefaultDriverName = null)
    {
        var env = getEnv ?? Environment.GetEnvironmentVariable;
        var diffOverride = env("CODA_TUI_DIFF")?.Trim();
        if (diffOverride is not null &&
            (diffOverride.Equals("0", StringComparison.OrdinalIgnoreCase) ||
             diffOverride.Equals("false", StringComparison.OrdinalIgnoreCase) ||
             diffOverride.Equals("off", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // A null/blank name means Terminal.Gui will pick the platform default. Resolve it to the
        // actual default driver name so the comparison below works correctly on every platform.
        var name = string.IsNullOrWhiteSpace(resolvedDriverName)
            ? (getDefaultDriverName ?? GetPlatformDefaultDriverName)()
            : resolvedDriverName;

        return string.Equals(name, DriverRegistry.Names.ANSI, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPlatformDefaultDriverName()
        => DriverRegistry.GetDefaultDriver().Name;

    /// <summary>
    /// Keeps native modified Enter unchanged and passes unknown keys through for future driver-specific
    /// normalization without changing existing input behavior.
    /// </summary>
    public static Key NormalizeModifiedEnter(Key key)
        => key == Key.Enter.WithShift ? Key.Enter.WithShift : key;
}
