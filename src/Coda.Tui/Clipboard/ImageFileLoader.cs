using System;
using System.IO;
using Coda.Sdk;

namespace Coda.Tui.Clipboard;

/// <summary>
/// Reads an image file named by a pasted path into the same <see cref="ClipboardImage"/> the clipboard
/// readers produce, so both routes converge on one staging path.
/// </summary>
/// <remarks>
/// Every refusal is reported rather than thrown: a paste that cannot become an attachment must still
/// leave the composer usable and say why, never take the UI down.
/// </remarks>
internal static class ImageFileLoader
{
    /// <summary>The largest file that may be attached, matching <c>/image</c>.</summary>
    private const long MaxFileSizeBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Loads <paramref name="path"/> as an attachable image of <paramref name="mediaType"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> with <paramref name="image"/> set on success; otherwise
    /// <see langword="false"/> with <paramref name="error"/> explaining what was wrong.
    /// </returns>
    public static bool TryLoad(string path, string mediaType, out ClipboardImage? image, out string? error)
    {
        image = null;
        error = null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = $"File not found: {Path.GetFileName(path)}";
                return false;
            }

            if (info.Length > MaxFileSizeBytes)
            {
                error = $"Image too large ({info.Length / (1024.0 * 1024.0):F1} MB). Maximum is 5 MB.";
                return false;
            }

            var bytes = File.ReadAllBytes(path);
            var base64 = Convert.ToBase64String(bytes);

            // The extension only claims what the file is; the validator checks the bytes agree.
            var validationError = ImageAttachmentValidation.Validate(mediaType, base64);
            if (validationError is not null)
            {
                error = validationError;
                return false;
            }

            // A path paste is a guess about intent, so the file has to prove it really is the image its
            // name promises. Attaching a renamed text file would waste a vision request and confuse the
            // model, and the signature costs a handful of bytes to check.
            if (!HasSignatureFor(mediaType, bytes))
            {
                error = $"Not a valid {mediaType.Replace("image/", string.Empty, StringComparison.Ordinal)} image";
                return false;
            }

            image = new ClipboardImage(mediaType, base64, bytes.Length);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            error = "Image could not be read";
            return false;
        }
    }

    /// <summary>Whether <paramref name="bytes"/> begins with the magic number for <paramref name="mediaType"/>.</summary>
    private static bool HasSignatureFor(string mediaType, ReadOnlySpan<byte> bytes) => mediaType switch
    {
        "image/png" => bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "image/jpeg" => bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),

        // RIFF....WEBP — the four size bytes between the two tags are not part of the signature.
        "image/webp" => bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };
}
