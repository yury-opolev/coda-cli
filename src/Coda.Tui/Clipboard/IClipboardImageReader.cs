namespace Coda.Tui.Clipboard;

/// <summary>
/// Reads an image from the OS clipboard. Never throws; returns null on absence, unsupported content,
/// tool-missing, or timeout. Shells out to an OS tool with a bounded child-process timeout.
/// </summary>
public interface IClipboardImageReader
{
    /// <summary>Try to read a PNG image from the OS clipboard. Returns null when unavailable.</summary>
    ClipboardImage? TryRead();
}
