using System.Diagnostics;
using LlmAuth;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// Default <see cref="IUrlOpener"/> implementation. Validates http/https before launching
/// via <see cref="Process.Start"/> with <c>UseShellExecute = true</c> (default browser) or
/// an explicit executable path for private-mode launches. Never performs shell-string
/// interpolation; all arguments are passed as separate <see cref="ProcessStartInfo"/> fields.
/// </summary>
internal sealed class DefaultUrlOpener : IUrlOpener
{
    /// <summary>The production singleton. Tests inject a separate instance with a custom process starter.</summary>
    public static readonly DefaultUrlOpener Instance = new();

    /// <summary>
    /// Seam for tests: if non-null, replaces the real <see cref="Process.Start"/> call.
    /// The delegate receives the <see cref="ProcessStartInfo"/> and returns true on success.
    /// </summary>
    internal Func<ProcessStartInfo, bool>? ProcessStarterOverride { get; init; }

    /// <inheritdoc />
    public bool TryOpen(string url, out string? error)
    {
        if (!ValidateHttpScheme(url, out error))
        {
            return false;
        }

        return this.LaunchProcess(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        }, out error);
    }

    /// <inheritdoc />
    public bool TryOpenPrivate(string url, PrivateBrowserInfo browser, out string? error)
    {
        if (!ValidateHttpScheme(url, out error))
        {
            return false;
        }

        // URL is passed as a SEPARATE argument — never shell-interpolated.
        var psi = new ProcessStartInfo
        {
            FileName = browser.ExePath,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(browser.PrivateFlag);
        psi.ArgumentList.Add(url);

        return this.LaunchProcess(psi, out error);
    }

    /// <summary>
    /// Validates that <paramref name="url"/> is an absolute http or https URI.
    /// </summary>
    internal static bool ValidateHttpScheme(string url, out string? error)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            error = "Only http/https URLs can be opened";
            return false;
        }

        error = null;
        return true;
    }

    private bool LaunchProcess(ProcessStartInfo psi, out string? error)
    {
        // The ProcessStarterOverride takes unconditional precedence: tests that inject a fake
        // starter can observe launch signals even while the env-var suppression is active.
        if (this.ProcessStarterOverride is { } starter)
        {
            var ok = starter(psi);
            error = ok ? null : "Launch failed";
            return ok;
        }

        // When the suppression variable is set (always the case in test assemblies via the
        // module initialiser), return a silent success so no OS window is opened.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable)))
        {
            error = null;
            return true;
        }

        try
        {
            Process.Start(psi);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
