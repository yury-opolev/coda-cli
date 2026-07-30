using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Coda.Tui.Clipboard;

/// <summary>
/// Answers "is there an image on the Windows clipboard?" without opening the clipboard or starting a
/// process.
/// </summary>
/// <remarks>
/// The only way to actually decode a clipboard bitmap from this process is to shell out to PowerShell
/// and load WinForms, which costs the better part of two seconds. Doing that on every paste — including
/// the overwhelmingly common plain-text paste — froze the UI thread each time. <c>IsClipboardFormatAvailable</c>
/// answers the same question in microseconds, so the expensive path now runs only when it can succeed.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsClipboardFormats
{
    /// <summary>A device-independent bitmap, which is what a screenshot or a copied image arrives as.</summary>
    private const uint CF_DIB = 8;

    /// <summary>A device-dependent bitmap handle. Some sources publish only this.</summary>
    private const uint CF_BITMAP = 2;

    /// <summary>The DIBv5 form, published by newer sources and by anything with an alpha channel.</summary>
    private const uint CF_DIBV5 = 17;

    // DllImport rather than LibraryImport: the source generator requires AllowUnsafeBlocks, and enabling
    // unsafe code across the whole assembly is a poor trade for one boolean P/Invoke.
    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    /// <summary>
    /// Whether the clipboard currently holds any bitmap format. Never throws: a failure to ask is
    /// reported as "no image", which costs a paste that could not have worked anyway.
    /// </summary>
    public static bool HasImage()
    {
        try
        {
            return IsClipboardFormatAvailable(CF_DIB)
                || IsClipboardFormatAvailable(CF_BITMAP)
                || IsClipboardFormatAvailable(CF_DIBV5);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
