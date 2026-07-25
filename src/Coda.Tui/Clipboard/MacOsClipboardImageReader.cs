using Coda.Sdk;

namespace Coda.Tui.Clipboard;

/// <summary>
/// Reads a PNG image from the macOS clipboard. Uses a two-step approach:
/// 1. <c>osascript</c> saves the clipboard PNG to a temp file.
/// 2. <c>base64</c> encodes the file.
/// Never throws; returns null on any failure (no image, clipboard unavailable, tool missing).
/// </summary>
internal sealed class MacOsClipboardImageReader(IProcessRunner runner) : IClipboardImageReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Command constants for testability.
    internal const string OsaScriptFileName = "osascript";
    internal const string Base64FileName = "base64";

    /// <summary>Builds the osascript argument string that saves the clipboard PNG to <paramref name="tmpPath"/>.</summary>
    internal static string BuildOsaScriptArgs(string tmpPath) =>
        "-e \"set pngData to the clipboard as \u00abclass PNGf\u00bb\" " +
        $"-e \"set fh to open for access POSIX file \\\"{tmpPath}\\\" with write permission\" " +
        "-e \"set eof fh to 0\" " +
        "-e \"write pngData to fh\" " +
        "-e \"close access fh\"";

    public ClipboardImage? TryRead()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), $"coda_clip_{Guid.NewGuid():N}.png");
        try
        {
            var osaResult = Task.Run(() => runner.RunAsync(OsaScriptFileName, BuildOsaScriptArgs(tmpPath), Timeout))
                .GetAwaiter().GetResult();
            if (osaResult is null)
            {
                return null;
            }

            var b64 = Task.Run(() => runner.RunAsync(Base64FileName, $"-i \"{tmpPath}\"", Timeout))
                .GetAwaiter().GetResult()?.Trim();
            if (string.IsNullOrEmpty(b64))
            {
                return null;
            }

            b64 = b64.Replace("\n", string.Empty).Replace("\r", string.Empty);
            if (!ImageAttachmentValidation.TryDecodeBase64(b64, out var bytes))
            {
                return null;
            }

            return new ClipboardImage("image/png", b64, bytes.Length);
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(tmpPath);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}
