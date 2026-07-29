namespace Coda.Common;

/// <summary>
/// Validates a single-segment file-system name (a plugin directory, a skill directory, …)
/// that originates from untrusted input such as a <c>plugin.json</c>, a git URL, or a
/// <c>/skills new</c> argument.
/// </summary>
/// <remarks>
/// One validator serves every caller on purpose: two near-identical checks drift, and the
/// weaker of the two then becomes the bypass for the stronger one.
/// </remarks>
public static class SafeNameValidator
{
    /// <summary>
    /// Names that map to a device rather than a file on Windows. Creating or deleting a
    /// directory with one of these names behaves unpredictably, so they are rejected on every
    /// platform to keep a repository portable.
    /// </summary>
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="name"/> is a safe single-segment
    /// name.
    /// </summary>
    /// <param name="name">The candidate name.</param>
    /// <remarks>
    /// A name is rejected when it is empty or whitespace, contains a path separator, contains
    /// <c>..</c> anywhere, contains a character that is invalid in a file name, starts or ends
    /// with whitespace, ends with a dot, or matches a reserved Windows device name (with or
    /// without an extension).
    /// </remarks>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        // Leading/trailing spaces and trailing dots are silently stripped by Windows, so a name
        // that relies on them does not round-trip: "foo." and "foo" would collide.
        if (name != name.Trim() || name.EndsWith('.'))
        {
            return false;
        }

        // "." and ".." are relative-path segments; any embedded ".." is a traversal attempt.
        if (name == "." || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        // Path.GetInvalidFileNameChars() is platform-specific (on Unix it holds only '\0' and
        // '/'), so both separators are rejected explicitly.
        if (name.Contains('/') || name.Contains('\\'))
        {
            return false;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in name)
        {
            if (Array.IndexOf(invalidChars, c) >= 0)
            {
                return false;
            }
        }

        return !IsReservedDeviceName(name);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="name"/> is a reserved Windows device
    /// name. The comparison ignores any extension, because <c>CON.txt</c> is the console too.
    /// </summary>
    private static bool IsReservedDeviceName(string name)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = dot >= 0 ? name[..dot] : name;

        foreach (var reserved in ReservedDeviceNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
