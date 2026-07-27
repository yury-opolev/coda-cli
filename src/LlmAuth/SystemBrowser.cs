using System.Diagnostics;

namespace LlmAuth;

/// <summary>
/// Default <see cref="LoginOptions.OpenBrowser"/> implementation.
/// In production the environment variable is absent and browser launch proceeds normally.
/// In test processes the module initializer sets <see cref="SuppressEnvironmentVariable"/>
/// so that any code path reaching this method returns silently without opening a window.
/// </summary>
public static class SystemBrowser
{
    /// <summary>
    /// Environment variable name that, when set to any non-empty value, causes
    /// <see cref="OpenAsync"/> to return immediately without launching a browser.
    ///
    /// <para>
    /// An environment variable is used rather than a mutable static delegate because
    /// xUnit runs tests in parallel; a static seam would be racy, while a variable set
    /// once at module load is process-wide and effectively immutable for the lifetime of
    /// the run.  The suppression returns silently (rather than throwing) so tests that
    /// merely touch an auth path incidentally stay green instead of becoming noisy failures.
    /// </para>
    /// </summary>
    public const string SuppressEnvironmentVariable = "CODA_NO_BROWSER_LAUNCH";

    /// <summary>
    /// Seam for serialized tests that need to verify launch behaviour without opening a real
    /// browser. When non-null, replaces the real <see cref="Process.Start"/> call.
    /// The delegate receives the <see cref="ProcessStartInfo"/> that would have been used and
    /// returns <see langword="true"/> on success.
    ///
    /// <para>
    /// This seam is intentionally not thread-safe. It must only be used from test classes that
    /// carry a <c>DisableParallelization = true</c> collection, because it is a mutable
    /// process-wide field. The <see cref="SuppressEnvironmentVariable"/> remains the correct
    /// parallel-safe suppression mechanism.
    /// </para>
    /// </summary>
    internal static Func<ProcessStartInfo, bool>? LauncherOverride { get; set; }

    /// <summary>Open a URL in the OS default browser (Windows/macOS/Linux).</summary>
    public static Task OpenAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        // When the suppression variable is set (always the case in test assemblies via the
        // module initializer, never the case in production where the variable is absent),
        // return without launching anything.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SuppressEnvironmentVariable)))
        {
            return Task.CompletedTask;
        }

        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo(url.ToString()) { UseShellExecute = true };
        }
        else if (OperatingSystem.IsMacOS())
        {
            psi = new ProcessStartInfo("open");
            psi.ArgumentList.Add(url.ToString());
        }
        else
        {
            psi = new ProcessStartInfo("xdg-open");
            psi.ArgumentList.Add(url.ToString());
        }

        if (LauncherOverride is { } launcher)
        {
            if (!launcher(psi))
            {
                throw new LlmAuthException(
                    $"Could not open a browser automatically. Visit this URL to continue:\n{url}");
            }
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new LlmAuthException(
                $"Could not open a browser automatically. Visit this URL to continue:\n{url}", ex);
        }

        return Task.CompletedTask;
    }
}
