using Coda.Sdk;

namespace Coda.Tui.Clipboard;

/// <summary>
/// Reads a PNG image from the Windows clipboard via a PowerShell one-liner that saves the clipboard
/// bitmap to a MemoryStream and base64-encodes it. Never throws.
/// </summary>
/// <remarks>
/// That one-liner has to load WinForms, which costs the better part of two seconds, so it is guarded by
/// <paramref name="hasImage"/> — a native format check that answers in microseconds. Without the guard
/// every paste, text included, froze the UI thread for the full cost of the probe.
/// </remarks>
internal sealed class WindowsClipboardImageReader(IProcessRunner runner, Func<bool>? hasImage = null)
    : IClipboardImageReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Func<bool> hasImage = hasImage ?? DefaultHasImage;

    // PowerShell script: load WinForms, get clipboard image, encode as PNG → base64.
    internal const string FileName = "powershell";

    internal const string Arguments =
        "-sta -Command \"Add-Type -AssemblyName System.Windows.Forms; " +
        "$img = [System.Windows.Forms.Clipboard]::GetImage(); " +
        "if ($img -eq $null) { exit 1 }; " +
        "$ms = New-Object System.IO.MemoryStream; " +
        "$img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); " +
        "[Convert]::ToBase64String($ms.ToArray())\"";

    /// <summary>The native format check, used on Windows and answered as "no image" anywhere else.</summary>
    private static bool DefaultHasImage() =>
        OperatingSystem.IsWindows() && WindowsClipboardFormats.HasImage();

    public ClipboardImage? TryRead()
    {
        try
        {
            if (!this.hasImage())
            {
                return null;
            }

            var b64 = Task.Run(() => runner.RunAsync(FileName, Arguments, Timeout)).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(b64))
            {
                return null;
            }

            b64 = b64.Trim();
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
    }
}
