namespace Coda.Tui.Ui.Shells;

/// <summary>
/// Opens an http/https URL in the OS browser. Injectable so tests never launch a real browser.
/// Every open path validates the URL (http/https only, <see cref="Uri.TryCreate"/> checked)
/// before launching any process; non-http(s) and malformed URLs are rejected without throwing.
/// </summary>
public interface IUrlOpener
{
    /// <summary>
    /// Attempts to open <paramref name="url"/> in the OS default browser.
    /// Returns <see langword="true"/> on success; <see langword="false"/> when the URL is invalid
    /// (non-http/https, malformed) or the launch fails, with a brief human-readable
    /// <paramref name="error"/> message suitable for the status row.
    /// Never throws.
    /// </summary>
    bool TryOpen(string url, out string? error);

    /// <summary>
    /// Attempts to open <paramref name="url"/> in a private/incognito browser window using the
    /// resolved <paramref name="browser"/> executable and its private-mode flag.
    /// The URL is passed as a SEPARATE argument (never interpolated into a shell command).
    /// Returns <see langword="false"/> and sets <paramref name="error"/> when the URL is invalid
    /// or the launch fails. Never throws.
    /// </summary>
    bool TryOpenPrivate(string url, PrivateBrowserInfo browser, out string? error);
}
