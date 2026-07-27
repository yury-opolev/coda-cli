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
    /// <exception cref="LlmAuthException">
    /// <paramref name="url"/> uses a scheme that is not allowed by the safety policy.
    /// </exception>
    public static Task OpenAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        // Validate the scheme BEFORE checking the suppression variable so that a hostile URL
        // from attacker-controlled OAuth metadata is always reported — even in CI/headless
        // environments where the browser would never open.  Silently short-circuiting on
        // suppression would hide the attack rather than alerting the operator.
        ValidateScheme(url);

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

    /// <summary>
    /// Validates that <paramref name="url"/> uses a scheme safe for OS-level launch.
    /// </summary>
    /// <remarks>
    /// Policy: <c>https</c> is always allowed. <c>http</c> is allowed only when the host is
    /// loopback (<see cref="Uri.IsLoopback"/>), because local OAuth redirect/dev servers
    /// legitimately use <c>http://127.0.0.1</c> or <c>http://localhost</c>. Everything else —
    /// including <c>ms-msdt:</c>, <c>file://</c>, <c>search-ms:</c>, and any non-loopback
    /// <c>http</c> host — is rejected.
    ///
    /// <para>
    /// On Windows, <see cref="ProcessStartInfo.UseShellExecute"/> routes the launch through
    /// ShellExecute, which invokes ANY registered protocol handler for the scheme.
    /// An attacker-controlled <c>.mcp.json</c> can supply a hostile URL as
    /// <c>authorization_endpoint</c>; without scheme validation this enables Follina-class
    /// code execution with no user confirmation prompt.
    /// </para>
    /// </remarks>
    /// <exception cref="LlmAuthException">
    /// <paramref name="url"/> uses a disallowed scheme.
    /// </exception>
    private static void ValidateScheme(Uri url)
    {
        if (string.Equals(url.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(url.Scheme, "http", StringComparison.OrdinalIgnoreCase) && url.IsLoopback)
        {
            return;
        }

        throw new LlmAuthException(
            $"Could not open a browser automatically. Visit this URL to continue:\n{url}");
    }
}
