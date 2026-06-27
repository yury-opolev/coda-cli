using System.Diagnostics;

namespace LlmAuth;

/// <summary>Default <see cref="LoginOptions.OpenBrowser"/> implementation.</summary>
public static class SystemBrowser
{
    /// <summary>Open a URL in the OS default browser (Windows/macOS/Linux).</summary>
    public static Task OpenAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url.ToString());
            }
            else
            {
                Process.Start("xdg-open", url.ToString());
            }
        }
        catch (Exception ex)
        {
            throw new LlmAuthException(
                $"Could not open a browser automatically. Visit this URL to continue:\n{url}", ex);
        }

        return Task.CompletedTask;
    }
}
