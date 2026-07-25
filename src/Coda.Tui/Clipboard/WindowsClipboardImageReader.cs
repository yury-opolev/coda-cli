using Coda.Sdk;

namespace Coda.Tui.Clipboard;

/// <summary>
/// Reads a PNG image from the Windows clipboard via a PowerShell one-liner that saves the clipboard
/// bitmap to a MemoryStream and base64-encodes it. Never throws.
/// </summary>
internal sealed class WindowsClipboardImageReader(IProcessRunner runner) : IClipboardImageReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // PowerShell script: load WinForms, get clipboard image, encode as PNG → base64.
    internal const string FileName = "powershell";

    internal const string Arguments =
        "-sta -Command \"Add-Type -AssemblyName System.Windows.Forms; " +
        "$img = [System.Windows.Forms.Clipboard]::GetImage(); " +
        "if ($img -eq $null) { exit 1 }; " +
        "$ms = New-Object System.IO.MemoryStream; " +
        "$img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); " +
        "[Convert]::ToBase64String($ms.ToArray())\"";

    public ClipboardImage? TryRead()
    {
        try
        {
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
