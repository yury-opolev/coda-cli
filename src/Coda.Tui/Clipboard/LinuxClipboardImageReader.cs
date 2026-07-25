using Coda.Sdk;

namespace Coda.Tui.Clipboard;

/// <summary>
/// Reads a PNG image from the Linux clipboard. Tries Wayland's <c>wl-paste</c> first,
/// then X11's <c>xclip</c>. Neither present → returns null (graceful no-op). Never throws.
/// The raw PNG bytes are base64-encoded by piping through <c>base64</c> inside a <c>bash -c</c>
/// invocation so <see cref="IProcessRunner"/> can capture printable stdout.
/// </summary>
internal sealed class LinuxClipboardImageReader(IProcessRunner runner) : IClipboardImageReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal const string WlPasteCommand = "wl-paste -t image/png";
    internal const string XclipCommand = "xclip -selection clipboard -t image/png -o";

    public ClipboardImage? TryRead()
    {
        try
        {
            var b64 = this.TryReadTool(WlPasteCommand) ?? this.TryReadTool(XclipCommand);
            if (b64 is null)
            {
                return null;
            }

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

    private string? TryReadTool(string command)
    {
        var shellCommand = $"{command} | base64 -w 0";
        var result = Task.Run(() => runner.RunAsync("bash", $"-c \"{shellCommand}\"", Timeout))
            .GetAwaiter().GetResult()?.Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
