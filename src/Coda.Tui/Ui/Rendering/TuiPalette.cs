using TgColor = Terminal.Gui.Drawing.Color;
using TgName = Terminal.Gui.Drawing.ColorName16;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// The named base colours a theme is built from. Semantic roles resolve through these rather than each
/// carrying its own literal RGB, so re-tinting a theme means changing four named colours instead of every
/// role that happens to share a hue. The TUI counterpart of <see cref="ConsolePalette"/>, which plays the
/// same part for non-TUI output.
/// </summary>
internal sealed record TuiPalette
{
    /// <summary>An outcome that needs no attention: everything succeeded.</summary>
    public TuiThemeColor Success { get; init; } = new(new TgColor(110, 180, 85), TgName.BrightGreen);

    /// <summary>An outcome that is noteworthy but not wrong: a partial result, an approval, a caveat.</summary>
    public TuiThemeColor Caution { get; init; } = new(new TgColor(240, 199, 94), TgName.Yellow);

    /// <summary>A failure or a rejection — and nothing else. This is the only red a theme should offer.</summary>
    public TuiThemeColor Danger { get; init; } = new(new TgColor(217, 104, 93), TgName.Red);

    /// <summary>Low-emphasis informational chrome.</summary>
    public TuiThemeColor Info { get; init; } = new(new TgColor(191, 174, 156), TgName.Gray);

    /// <summary>The Warm Ember base palette, and the defaults every omitted entry inherits.</summary>
    public static TuiPalette WarmEmber { get; } = new();
}
