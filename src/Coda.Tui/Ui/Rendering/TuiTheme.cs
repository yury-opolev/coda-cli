using Terminal.Gui.Drivers;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;
using TgName = Terminal.Gui.Drawing.ColorName16;
using TgScheme = Terminal.Gui.Drawing.Scheme;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// A single semantic role color: an exact 24-bit RGB value for true-color terminals plus a named
/// 16-color <see cref="Fallback"/> used when the driver cannot render true color (or is forced to 16
/// colors). <see cref="TuiTheme.Resolve"/> picks between the two.
/// </summary>
internal readonly record struct TuiThemeColor(TgColor TrueColor, TgName Fallback);

/// <summary>
/// The Warm Ember palette: one immutable, semantic theme shared by every retained TUI surface so no
/// view carries its own hard-coded colors. Each role exposes an exact true-color RGB plus a named
/// 16-color fallback, and the theme resolves a role to a concrete <see cref="TgColor"/> or a full
/// <see cref="TgScheme"/> based on the active driver's true-color support.
/// </summary>
/// <remarks>
/// Colors are expressed through fully-qualified <see cref="Terminal.Gui.Drawing"/> types (aliased here)
/// so the global <c>Color = Spectre.Console.Color</c> alias never leaks in. The palette leans warm amber
/// and coral against a near-black background; the named fallbacks are chosen to degrade cleanly on
/// low-color terminals (e.g. tool output stays yellow rather than blue, approvals stay red rather than
/// magenta).
/// </remarks>
internal sealed class TuiTheme
{
    /// <summary>The single shared Warm Ember theme instance.</summary>
    public static TuiTheme WarmEmber { get; } = new();

    private TuiTheme()
    {
    }

    public TuiThemeColor Background { get; } = new(new TgColor(23, 19, 16), TgName.Black);
    public TuiThemeColor TranscriptAssistant { get; } = new(new TgColor(242, 214, 179), TgName.White);
    public TuiThemeColor TranscriptUser { get; } = new(new TgColor(230, 168, 74), TgName.BrightYellow);
    public TuiThemeColor Heading { get; } = new(new TgColor(240, 179, 91), TgName.BrightYellow);
    public TuiThemeColor Code { get; } = new(new TgColor(200, 184, 166), TgName.Gray);
    public TuiThemeColor TranscriptTool { get; } = new(new TgColor(215, 168, 75), TgName.BrightYellow);
    public TuiThemeColor Diff { get; } = new(new TgColor(201, 138, 82), TgName.Yellow);
    public TuiThemeColor PermissionApproval { get; } = new(new TgColor(233, 130, 107), TgName.BrightRed);
    public TuiThemeColor Question { get; } = new(new TgColor(240, 199, 94), TgName.BrightYellow);
    public TuiThemeColor Warning { get; } = new(new TgColor(240, 199, 94), TgName.Yellow);
    public TuiThemeColor Notification { get; } = new(new TgColor(191, 174, 156), TgName.Gray);
    public TuiThemeColor Error { get; } = new(new TgColor(217, 104, 93), TgName.Red);

    public TuiThemeColor ComposerText { get; } = new(new TgColor(242, 214, 179), TgName.White);
    public TuiThemeColor ComposerPrompt { get; } = new(new TgColor(230, 168, 74), TgName.BrightYellow);

    public TuiThemeColor OperationalReady { get; } = new(new TgColor(143, 136, 128), TgName.Gray);
    public TuiThemeColor OperationalInitializing { get; } = new(new TgColor(179, 138, 80), TgName.Yellow);
    public TuiThemeColor OperationalWorking { get; } = new(new TgColor(229, 139, 54), TgName.BrightYellow);
    public TuiThemeColor OperationalThinking { get; } = new(new TgColor(216, 94, 94), TgName.BrightRed);
    public TuiThemeColor OperationalWaiting { get; } = new(new TgColor(143, 136, 128), TgName.Gray);

    public TuiThemeColor CompletionNormal { get; } = new(new TgColor(215, 194, 168), TgName.White);
    public TuiThemeColor CompletionSelectedText { get; } = new(new TgColor(23, 19, 16), TgName.Black);
    public TuiThemeColor CompletionSelectedBackground { get; } = new(new TgColor(230, 168, 74), TgName.BrightYellow);

    public TuiThemeColor PromptText { get; } = new(new TgColor(242, 214, 179), TgName.White);
    public TuiThemeColor PromptAccent { get; } = new(new TgColor(233, 130, 107), TgName.BrightRed);
    public TuiThemeColor SelectionText { get; } = new(new TgColor(23, 19, 16), TgName.Black);
    public TuiThemeColor SelectionBackground { get; } = new(new TgColor(230, 168, 74), TgName.BrightYellow);

    /// <summary>Resolves a role to its exact RGB (true color) or its named 16-color fallback.</summary>
    public static TgColor Resolve(TuiThemeColor role, bool trueColor) =>
        trueColor ? role.TrueColor : new TgColor(role.Fallback);

    /// <summary>Whether the driver can render 24-bit color and is not forced to a 16-color palette.</summary>
    public static bool SupportsTrueColor(IDriver? driver) =>
        driver is { SupportsTrueColor: true, Force16Colors: false };

    /// <summary>Builds a foreground/background attribute for the driver's color depth.</summary>
    public TgAttribute Attribute(TuiThemeColor foreground, TuiThemeColor background, IDriver? driver) =>
        new(Resolve(foreground, SupportsTrueColor(driver)), Resolve(background, SupportsTrueColor(driver)));

    /// <summary>A solid dark composer scheme keyed to the driver's color depth.</summary>
    public TgScheme ComposerScheme(IDriver? driver)
    {
        var normal = this.Attribute(this.ComposerText, this.Background, driver);
        var focus = this.Attribute(this.TranscriptAssistant, this.Background, driver);
        return SolidScheme(normal, focus);
    }

    /// <summary>
    /// The top-level surface scheme: a neutral warm foreground (<see cref="TranscriptAssistant"/>) over the
    /// Warm Ember <see cref="Background"/> for <em>every</em> scheme state, keyed to the driver's color depth.
    /// Applied to the retained shell so header, status, transcript, and completion — none of which carry an
    /// explicit scheme — inherit one uniform background regardless of focus/active/disabled state.
    /// </summary>
    public TgScheme SurfaceScheme(IDriver? driver)
    {
        var normal = this.Attribute(this.TranscriptAssistant, this.Background, driver);
        return SolidScheme(normal, normal);
    }

    /// <summary>A solid dark prompt-overlay scheme keyed to the driver's color depth.</summary>
    public TgScheme PromptScheme(IDriver? driver)
    {
        var normal = this.Attribute(this.PromptText, this.Background, driver);
        var focus = this.Attribute(this.PromptAccent, this.Background, driver);
        return SolidScheme(normal, focus);
    }

    private static TgScheme SolidScheme(TgAttribute normal, TgAttribute focus) => new()
    {
        Normal = normal,
        HotNormal = normal,
        Focus = focus,
        HotFocus = focus,
        Active = focus,
        HotActive = focus,
        Highlight = focus,
        Editable = normal,
        ReadOnly = normal,
        Disabled = normal,
    };
}
