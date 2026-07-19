namespace Coda.Tui.Ui.Prompts;

/// <summary>
/// Produces the shared plain-text label for a <see cref="UiPromptOption"/>. Both the Terminal.Gui
/// overlay and the Spectre fallback consume this single formatting responsibility so current-state
/// options render identically regardless of the active renderer.
/// </summary>
internal static class UiPromptOptionFormatter
{
    /// <summary>The glyph marking the option that reflects the current state.</summary>
    internal const string CurrentMarker = "●";

    /// <summary>Formats <paramref name="option"/> as plain text, prefixing and annotating the current option.</summary>
    internal static string Format(UiPromptOption option)
    {
        ArgumentNullException.ThrowIfNull(option);

        var prefix = option.IsCurrent ? $"{CurrentMarker} " : "  ";
        var detail = option.IsCurrent
            ? string.IsNullOrWhiteSpace(option.Detail)
                ? "Current"
                : $"{option.Detail} · Current"
            : option.Detail;

        return string.IsNullOrWhiteSpace(detail)
            ? $"{prefix}{option.Label}"
            : $"{prefix}{option.Label} — {detail}";
    }
}
