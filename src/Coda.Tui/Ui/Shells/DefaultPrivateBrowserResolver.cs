using Microsoft.Win32;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// Best-effort Windows implementation of <see cref="IPrivateBrowserResolver"/>. It probes the
/// Windows registry <c>App Paths</c> key and standard <c>Program Files</c> install locations for
/// Chrome, Edge, Firefox, and Brave, then returns the first found browser with its private-mode
/// flag, or <see langword="null"/> when none is detected. The interface is OS-neutral; this
/// implementation is Windows-focused and gracefully returns null on non-Windows platforms.
/// </summary>
internal sealed class DefaultPrivateBrowserResolver : IPrivateBrowserResolver
{
    /// <summary>The production singleton.</summary>
    public static readonly DefaultPrivateBrowserResolver Instance = new();

    private static readonly (string ExeName, string PrivateFlag)[] KnownBrowsers =
    [
        ("msedge.exe",  "--inprivate"),
        ("chrome.exe",  "--incognito"),
        ("brave.exe",   "--incognito"),
        ("firefox.exe", "-private-window"),
    ];

    /// <inheritdoc />
    public PrivateBrowserInfo? Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var (exeName, flag) in KnownBrowsers)
        {
            var path = FindViaAppPaths(exeName) ?? FindViaStandardPaths(exeName);
            if (path is not null)
            {
                return new PrivateBrowserInfo(path, flag);
            }
        }

        return null;
    }

    /// <summary>Queries the <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths</c> registry key.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FindViaAppPaths(string exeName)
    {
        try
        {
            const string appPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
            using var key = Registry.LocalMachine.OpenSubKey($@"{appPathsKey}\{exeName}");
            var value = key?.GetValue(null) as string;
            if (!string.IsNullOrEmpty(value) && File.Exists(value))
            {
                return value;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Probes common <c>Program Files</c> install locations.</summary>
    private static string? FindViaStandardPaths(string exeName)
    {
        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        var subdirectories = exeName switch
        {
            "msedge.exe"  => new[] { @"Microsoft\Edge\Application" },
            "chrome.exe"  => new[] { @"Google\Chrome\Application" },
            "brave.exe"   => new[] { @"BraveSoftware\Brave-Browser\Application" },
            "firefox.exe" => new[] { @"Mozilla Firefox" },
            _             => Array.Empty<string>(),
        };

        foreach (var root in programFiles)
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var sub in subdirectories)
            {
                var candidate = Path.Combine(root, sub, exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
