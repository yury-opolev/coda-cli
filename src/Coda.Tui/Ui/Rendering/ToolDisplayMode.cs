namespace Coda.Tui.Ui.Rendering;

/// <summary>Controls how tool activity is presented by human-facing renderers.</summary>
public enum ToolDisplayMode
{
    Verbose,
    Compact,
    Tiny,
}

/// <summary>The resolved display mode plus the raw setting information needed for diagnostics.</summary>
public readonly record struct ToolDisplayModeResolution(
    ToolDisplayMode Mode,
    bool IsValid,
    string? RawValue);

/// <summary>Pure resolver for the user-facing tool display mode setting.</summary>
public static class ToolDisplayModeResolver
{
    public const int CompactInputPreviewMax = 128;

    /// <summary>
    /// Resolves a raw setting and reports whether it was an unrecognized non-blank value.
    /// Missing and blank values use the default without producing an invalid-value warning.
    /// </summary>
    public static ToolDisplayMode Resolve(string? rawValue, out bool wasInvalid)
    {
        var resolution = Resolve(rawValue);
        wasInvalid = !resolution.IsValid;
        return resolution.Mode;
    }

    /// <summary>
    /// Resolves a raw setting value. Missing, blank, and unrecognized values use the quiet default
    /// <see cref="ToolDisplayMode.Tiny"/> and report <see cref="ToolDisplayModeResolution.IsValid"/> as false.
    /// </summary>
    public static ToolDisplayModeResolution Resolve(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new(ToolDisplayMode.Tiny, true, rawValue);
        }

        var mode = rawValue.Trim().ToLowerInvariant() switch
        {
            "verbose" => ToolDisplayMode.Verbose,
            "compact" => ToolDisplayMode.Compact,
            "tiny" => ToolDisplayMode.Tiny,
            _ => (ToolDisplayMode?)null,
        };

        return mode is { } resolved
            ? new(resolved, true, rawValue)
            : new(ToolDisplayMode.Tiny, false, rawValue);
    }
}

internal static class ToolDisplayModeText
{
    public static string ArgumentPreview(string? input)
    {
        var sanitized = TerminalTextSanitizer.Sanitize(input);
        var builder = new System.Text.StringBuilder(
            Math.Min(ToolDisplayModeResolver.CompactInputPreviewMax, sanitized.Length));
        var pendingSpace = false;

        foreach (var character in sanitized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
            if (builder.Length == ToolDisplayModeResolver.CompactInputPreviewMax)
            {
                break;
            }
        }

        return builder.ToString();
    }
}
