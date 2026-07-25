namespace Coda.Tui.Clipboard;

/// <summary>Runs a child process and captures its stdout. Test seam for per-OS command construction.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/>, returns stdout on success.
    /// Returns null when the process exits non-zero or times out.
    /// </summary>
    Task<string?> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken = default);
}
