namespace Coda.Tui.Ui.Shells;

/// <summary>
/// A private-capable browser resolved by <see cref="IPrivateBrowserResolver"/>.
/// </summary>
/// <param name="ExePath">Absolute path to the browser executable.</param>
/// <param name="PrivateFlag">
/// The command-line flag that enables the browser's private/incognito mode
/// (e.g. <c>--incognito</c> for Chrome, <c>--inprivate</c> for Edge,
/// <c>-private-window</c> for Firefox, <c>--incognito</c> for Brave).
/// </param>
public sealed record PrivateBrowserInfo(string ExePath, string PrivateFlag);

/// <summary>
/// Best-effort detection of a browser that supports a private/incognito window.
/// Returns <see langword="null"/> when no suitable browser is found on the current machine.
/// The menu item for "Open in private window" is hidden when <see cref="Resolve"/> returns null.
/// Injectable so tests never touch the file system or registry.
/// </summary>
public interface IPrivateBrowserResolver
{
    /// <summary>
    /// Probes well-known browser install locations and returns the first found browser with its
    /// private-mode flag, or <see langword="null"/> when none is detected.
    /// </summary>
    PrivateBrowserInfo? Resolve();
}
