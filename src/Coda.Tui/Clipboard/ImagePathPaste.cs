using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;

namespace Coda.Tui.Clipboard;

/// <summary>
/// Recognises a pasted path to an image file, so pasting one attaches the image rather than inserting
/// its path as text.
/// </summary>
/// <remarks>
/// This exists because of what a terminal emulator does to Ctrl+V. Windows Terminal claims that key for
/// its own paste action, and that action carries text only — a copied bitmap produces no input at all,
/// so the application cannot even know the key was pressed. What it does carry is a path, when the file
/// was copied with Explorer's "Copy as path". Treating such a paste as an attachment is therefore the
/// one way Ctrl+V can attach an image without the user reconfiguring their terminal.
/// <para>
/// Pure and host-neutral: it decides only whether the text names an image, never whether the file is
/// there. Existence and contents are the loader's business.
/// </para>
/// </remarks>
internal static class ImagePathPaste
{
    /// <summary>
    /// Image extensions that may be attached, and the media type each maps to. Deliberately the same set
    /// <c>/image</c> accepts, so a path attaches identically however it arrived.
    /// </summary>
    public static readonly FrozenDictionary<string, string> SupportedExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="pasted"/> is a path naming an image file, and if so the path with any
    /// quoting removed plus the media type its extension implies.
    /// </summary>
    /// <remarks>
    /// A payload spanning more than one line is always text, even when one of its lines looks like a
    /// path: swallowing a block of prose because it mentioned a file would be far worse than missing an
    /// attachment.
    /// </remarks>
    public static bool TryGetImagePath(string? pasted, out string path, out string mediaType)
    {
        path = string.Empty;
        mediaType = string.Empty;

        if (string.IsNullOrWhiteSpace(pasted))
        {
            return false;
        }

        var candidate = pasted.Trim();

        // Only an interior newline makes this a multi-line payload; a trailing one is just how the
        // clipboard ends a single line.
        if (candidate.AsSpan().ContainsAny('\n', '\r'))
        {
            return false;
        }

        // Explorer's "Copy as path" wraps the path in double quotes.
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1].Trim();
        }

        if (candidate.Length == 0)
        {
            return false;
        }

        string extension;
        try
        {
            extension = Path.GetExtension(candidate);
        }
        catch (ArgumentException)
        {
            // Invalid path characters — not a path we can attach.
            return false;
        }

        // A bare ".png" names an extension, not a file.
        if (extension.Length == 0 || extension.Length == candidate.Length)
        {
            return false;
        }

        if (!SupportedExtensions.TryGetValue(extension, out var resolved))
        {
            return false;
        }

        path = candidate;
        mediaType = resolved;
        return true;
    }
}
