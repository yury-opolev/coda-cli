using TgColor = Terminal.Gui.Drawing.Color;
using TgName = Terminal.Gui.Drawing.ColorName16;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// The named base colours a theme is built from. Semantic roles resolve through these rather than each
/// carrying its own literal RGB, so re-tinting a theme means changing four named colours instead of every
/// role that happens to share a hue. The TUI counterpart of <see cref="ConsolePalette"/>, and deliberately
/// sharing its vocabulary so one set of names covers both terminal surfaces.
/// </summary>
internal sealed record TuiPalette
{
    /// <summary>An outcome that needs no attention: everything succeeded.</summary>
    public TuiThemeColor Success { get; init; } = new(new TgColor(110, 180, 85), TgName.BrightGreen);

    /// <summary>An outcome that is noteworthy but not wrong: a partial result, an approval, a caveat.</summary>
    public TuiThemeColor Warn { get; init; } = new(new TgColor(240, 199, 94), TgName.Yellow);

    /// <summary>A failure or a rejection — and nothing else. This is the only red a theme should offer.</summary>
    public TuiThemeColor Error { get; init; } = new(new TgColor(217, 104, 93), TgName.Red);

    /// <summary>Low-emphasis informational chrome.</summary>
    public TuiThemeColor Dim { get; init; } = new(new TgColor(191, 174, 156), TgName.Gray);

    /// <summary>
    /// A neutral hue that carries no success/failure meaning — used where something must simply read
    /// as distinct, such as a keyword against a type in highlighted code.
    /// </summary>
    public TuiThemeColor Accent { get; init; } = new(new TgColor(150, 170, 220), TgName.BrightBlue);

    /// <summary>The Warm Ember base palette, and the defaults every omitted entry inherits.</summary>
    public static TuiPalette WarmEmber { get; } = new();
}
