namespace Coda.Sdk;

/// <summary>
/// Validates image attachments for MIME type, size, and base64 encoding — shared by the
/// clipboard paste path, the /image command, and the serve session/prompt handler.
/// </summary>
public static class ImageAttachmentValidation
{
    /// <summary>Maximum accepted image size: 5 MiB.</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>MIME types accepted as image attachments.</summary>
    public static readonly IReadOnlySet<string> AllowedMimeTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/png", "image/jpeg", "image/gif", "image/webp",
        };

    /// <summary>Returns true when <paramref name="mediaType"/> is in the allow-list.</summary>
    public static bool IsAllowedMimeType(string mediaType) =>
        AllowedMimeTypes.Contains(mediaType);

    /// <summary>
    /// Validates a base64-encoded image: MIME type allow-list, valid base64, and decoded size ≤ 5 MB.
    /// Returns a failure message, or null on success.
    /// </summary>
    public static string? Validate(string mediaType, string base64Data)
    {
        if (!IsAllowedMimeType(mediaType))
        {
            return $"Unsupported image type '{mediaType}'. Supported: png, jpeg, gif, webp.";
        }

        if (!TryDecodeBase64(base64Data, out var bytes))
        {
            return "Image base64 data is empty or invalid.";
        }

        if (bytes.Length > MaxBytes)
        {
            return $"Image too large ({bytes.Length / (1024.0 * 1024.0):F1} MB). Maximum is 5 MB.";
        }

        return null;
    }

    /// <summary>Tries to decode a base64 string; returns false when null, empty, or malformed.</summary>
    public static bool TryDecodeBase64(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
