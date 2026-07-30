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

    public TuiTheme()
    {
    }

    /// <summary>The named base colours this theme is built from. The outcome roles below resolve through it,
    /// so a theme re-tints them by supplying its own palette rather than restating each role.</summary>
    public TuiPalette Palette { get; init; } = TuiPalette.WarmEmber;

    public TuiThemeColor Background { get; init; } = new(new TgColor(23, 19, 16), TgName.Black);
    public TuiThemeColor TranscriptAssistant { get; init; } = new(new TgColor(242, 214, 179), TgName.White);
    public TuiThemeColor TranscriptUser { get; init; } = new(new TgColor(230, 168, 74), TgName.BrightYellow);

    /// <summary>A subtly different, slightly lighter warm near-black behind a submitted user message so it
    /// reads as its own full-width block. In 16-color mode it degrades to the shell background (no block).</summary>
    public TuiThemeColor TranscriptUserBackground { get; init; } = new(new TgColor(38, 30, 24), TgName.Black);

    /// <summary>The dim warm tone of the right-aligned sent-time indicator on a user message block.</summary>
    public TuiThemeColor TranscriptUserTime { get; init; } = new(new TgColor(150, 128, 104), TgName.Gray);
    public TuiThemeColor Heading { get; init; } = new(new TgColor(240, 179, 91), TgName.BrightYellow);
    public TuiThemeColor Code { get; init; } = new(new TgColor(200, 184, 166), TgName.Gray);
    public TuiThemeColor TranscriptTool { get; init; } = new(new TgColor(240, 190, 84), TgName.BrightYellow);
    public TuiThemeColor Diff { get; init; } = new(new TgColor(201, 138, 82), TgName.Yellow);

    /// <summary>A wholly successful batch of tool calls. The one green in the transcript palette: it means
    /// "nothing needs your attention", which is why it is never used for a partial outcome.
    /// Resolves through <see cref="TuiPalette.Success"/>.</summary>
    public TuiThemeColor ToolSuccess => this.Palette.Success;

    /// <summary>A batch of tool calls where some, but not all, failed. Noteworthy but not a failure.
    /// Resolves through <see cref="TuiPalette.Warn"/>.</summary>
    public TuiThemeColor ToolPartialFailure => this.Palette.Warn;

    /// <summary>A tool that ran after its permission was approved. Noteworthy rather than a failure.
    /// Resolves through <see cref="TuiPalette.Warn"/>.</summary>
    public TuiThemeColor PermissionApproved => this.Palette.Warn;

    /// <summary>An open question awaiting the user — a permission still to be decided, a prompt to answer.
    /// Resolves through <see cref="TuiPalette.Warn"/>, so the "waiting for approval" status row and an
    /// approved transcript row agree on the colour that means "this needs you, nothing has failed".</summary>
    public TuiThemeColor Question => this.Palette.Warn;

    /// <summary>A cancelled or partially-cancelled outcome. Resolves through <see cref="TuiPalette.Warn"/>.</summary>
    public TuiThemeColor Warning => this.Palette.Warn;

    /// <summary>Low-emphasis informational chrome. Resolves through <see cref="TuiPalette.Dim"/>.</summary>
    public TuiThemeColor Notification => this.Palette.Dim;

    /// <summary>A failure or a rejected permission — and nothing else.
    /// Resolves through <see cref="TuiPalette.Error"/>.</summary>
    public TuiThemeColor Error => this.Palette.Error;

    // Six eye-friendly Warm Ember context-usage colors, one per /context category. Each is a distinct
    // warm hue (gold → amber → terracotta → rose → taupe → dim warm grey) with a distinct, warm-degrading
    // 16-color fallback so the categories stay legible by color even when the driver drops to 16 colors.
    public TuiThemeColor ContextSystemPrompt { get; init; } = new(new TgColor(240, 190, 84), TgName.BrightYellow);
    public TuiThemeColor ContextSystemTools { get; init; } = new(new TgColor(222, 146, 74), TgName.Yellow);
    public TuiThemeColor ContextMcpTools { get; init; } = new(new TgColor(216, 122, 90), TgName.BrightRed);
    public TuiThemeColor ContextMessages { get; init; } = new(new TgColor(214, 96, 96), TgName.Red);
    public TuiThemeColor ContextAutocompactBuffer { get; init; } = new(new TgColor(168, 154, 134), TgName.Gray);
    public TuiThemeColor ContextFreeSpace { get; init; } = new(new TgColor(112, 102, 92), TgName.DarkGray);

    // Five GitHub-style callout roles (Note/Tip/Important/Warning/Caution). Each pairs with a matching
    // TranscriptRole so title rows resolve to the callout hue. Values here are the Warm Ember defaults;
    // Default and CoolDark built-ins override all five to their own palette (enforced by the parity test).
    public TuiThemeColor CalloutNote { get; init; } = new(new TgColor(150, 190, 230), TgName.BrightBlue);
    public TuiThemeColor CalloutTip { get; init; } = new(new TgColor(120, 190, 100), TgName.BrightGreen);
    public TuiThemeColor CalloutImportant { get; init; } = new(new TgColor(200, 140, 215), TgName.BrightMagenta);
    public TuiThemeColor CalloutWarning { get; init; } = new(new TgColor(235, 165, 45), TgName.Yellow);
    public TuiThemeColor CalloutCaution { get; init; } = new(new TgColor(220, 90, 70), TgName.BrightRed);

    /// <summary>Dim user foreground for a queued/not-yet-delivered pending user message. A muted version of
    /// the theme's user color so the whole pending block reads as unconfirmed until delivered.</summary>
    public TuiThemeColor PendingUser { get; init; } = new(new TgColor(150, 128, 104), TgName.Gray);

    // -----------------------------------------------------------------------
    // Rich diff coloring (added 2026-07 for richer git-diff rendering)
    // -----------------------------------------------------------------------

    /// <summary>Foreground for added lines in a rich diff block — resolves through <see cref="TuiPalette.Success"/>
    /// so a diff's green matches the tool-success green, keeping the palette consistent.</summary>
    public TuiThemeColor DiffAdded => this.Palette.Success;

    /// <summary>Foreground for removed lines in a rich diff block — resolves through <see cref="TuiPalette.Error"/>
    /// so the diff red aligns with errors and rejections throughout the transcript.</summary>
    public TuiThemeColor DiffRemoved => this.Palette.Error;

    /// <summary>Foreground for context lines, line-number gutters, and the summary row — resolves through
    /// <see cref="TuiPalette.Dim"/> to de-emphasise unchanged lines relative to the coloured add/remove rows.</summary>
    public TuiThemeColor DiffContext => this.Palette.Dim;

    /// <summary>Full-width background painted behind added lines so the entire viewport row reads as green
    /// rather than just the content characters. Per-theme init property so each theme can choose a dark
    /// shade that complements its overall background without looking like the tool-success foreground.</summary>
    public TuiThemeColor DiffAddedBackground { get; init; } = new(new TgColor(22, 52, 22), TgName.DarkGray);

    /// <summary>Full-width background painted behind removed lines. Per-theme init property matching the
    /// <see cref="DiffAddedBackground"/> discipline so themes remain internally consistent.</summary>
    public TuiThemeColor DiffRemovedBackground { get; init; } = new(new TgColor(52, 20, 20), TgName.DarkGray);

    /// <summary>Foreground color applied to honest link spans (display text identifies the destination).</summary>
    public TuiThemeColor Link { get; init; } = new(new TgColor(110, 165, 215), TgName.BrightBlue);
    /// <summary>Foreground color applied to deceptive link spans (display text hides the destination),
    /// including the trailing ⚠ warning glyph. Chosen to visually warn without being aggressive.</summary>
    public TuiThemeColor LinkDeceptive { get; init; } = new(new TgColor(215, 125, 55), TgName.Yellow);

    public TuiThemeColor ComposerText { get; init; } = new(new TgColor(242, 214, 179), TgName.White);
    public TuiThemeColor ComposerPrompt { get; init; } = new(new TgColor(230, 168, 74), TgName.BrightYellow);

    /// <summary>A slightly lighter warm near-black than <see cref="Background"/> so the composer input
    /// region reads as its own panel rather than blending into the transcript surface.</summary>
    public TuiThemeColor ComposerPanelBackground { get; init; } = new(new TgColor(34, 28, 23), TgName.Black);

    /// <summary>The half-block edge shading drawn along the composer panel's top and bottom rows: a warm
    /// tone a touch lighter than the panel so the seam between shell and panel is soft, not a hard border.</summary>
    public TuiThemeColor ComposerPanelEdge { get; init; } = new(new TgColor(58, 47, 38), TgName.Black);

    public TuiThemeColor OperationalReady { get; init; } = new(new TgColor(143, 136, 128), TgName.Gray);
    public TuiThemeColor OperationalInitializing { get; init; } = new(new TgColor(179, 138, 80), TgName.Yellow);
    public TuiThemeColor OperationalWorking { get; init; } = new(new TgColor(229, 139, 54), TgName.BrightYellow);
    public TuiThemeColor OperationalThinking { get; init; } = new(new TgColor(216, 94, 94), TgName.BrightRed);
    public TuiThemeColor OperationalWaiting { get; init; } = new(new TgColor(143, 136, 128), TgName.Gray);

    public TuiThemeColor CompletionNormal { get; init; } = new(new TgColor(215, 194, 168), TgName.White);
    public TuiThemeColor CompletionSelectedText { get; init; } = new(new TgColor(23, 19, 16), TgName.Black);
    public TuiThemeColor CompletionSelectedBackground { get; init; } = new(new TgColor(230, 168, 74), TgName.BrightYellow);

    public TuiThemeColor PromptText { get; init; } = new(new TgColor(242, 214, 179), TgName.White);
    public TuiThemeColor PromptAccent { get; init; } = new(new TgColor(233, 130, 107), TgName.BrightRed);
    public TuiThemeColor SelectionText { get; init; } = new(new TgColor(23, 19, 16), TgName.Black);
    public TuiThemeColor SelectionBackground { get; init; } = new(new TgColor(230, 168, 74), TgName.BrightYellow);
    public TuiThemeColor ScrollbarTrack { get; init; } = new(new TgColor(112, 102, 92), TgName.DarkGray);
    public TuiThemeColor ScrollbarThumb { get; init; } = new(new TgColor(230, 168, 74), TgName.BrightYellow);

    /// <summary>Resolves a role to its exact RGB (true color) or its named 16-color fallback.</summary>
    public static TgColor Resolve(TuiThemeColor role, bool trueColor) =>
        trueColor ? role.TrueColor : new TgColor(role.Fallback);

    /// <summary>Whether the driver can render 24-bit color and is not forced to a 16-color palette.</summary>
    public static bool SupportsTrueColor(IDriver? driver) =>
        driver is { SupportsTrueColor: true, Force16Colors: false };

    /// <summary>Builds a foreground/background attribute for the driver's color depth.</summary>
    public TgAttribute Attribute(TuiThemeColor foreground, TuiThemeColor background, IDriver? driver) =>
        new(Resolve(foreground, SupportsTrueColor(driver)), Resolve(background, SupportsTrueColor(driver)));

    public TgAttribute JumpHintAttribute(IDriver? driver) =>
        this.Attribute(this.Heading, this.ComposerPanelBackground, driver);

    public TgAttribute ScrollbarTrackAttribute(IDriver? driver) =>
        this.Attribute(this.ScrollbarTrack, this.Background, driver);

    public TgAttribute ScrollbarThumbAttribute(IDriver? driver) =>
        this.Attribute(this.ScrollbarThumb, this.Background, driver);

    /// <summary>A solid composer panel scheme keyed to the driver's color depth: the warm composer text
    /// over the distinct <see cref="ComposerPanelBackground"/> so the input region reads as its own panel.</summary>
    public TgScheme ComposerScheme(IDriver? driver)
    {
        var normal = this.Attribute(this.ComposerText, this.ComposerPanelBackground, driver);
        var focus = this.Attribute(this.TranscriptAssistant, this.ComposerPanelBackground, driver);
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
