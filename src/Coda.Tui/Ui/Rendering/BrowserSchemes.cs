using Terminal.Gui.Drivers;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgScheme = Terminal.Gui.Drawing.Scheme;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// Per-state <see cref="TgScheme"/>s for browser rows and cells, resolved from the theme's palette.
/// </summary>
/// <remarks>
/// A <see cref="TgScheme"/> is the unit <c>TableStyle.RowColorGetter</c> and
/// <c>ColumnStyle.ColorGetter</c> consume, so the browsers need schemes rather than the bare
/// <see cref="TuiThemeColor"/> roles they would otherwise reach for. Colours resolve through the
/// palette, so a re-tinted theme carries the browsers with it and no browser hard-codes a colour.
/// </remarks>
internal sealed class BrowserSchemes
{
    private readonly TuiTheme theme;
    private readonly IDriver? driver;

    public BrowserSchemes(TuiTheme theme, IDriver? driver)
    {
        this.theme = theme ?? throw new ArgumentNullException(nameof(theme));
        this.driver = driver;

        this.Normal = this.Build(theme.TranscriptAssistant);
        this.Healthy = this.Build(theme.Palette.Success);
        this.Idle = this.Build(theme.Palette.Dim);
        this.Disabled = this.Build(theme.Palette.Dim);
        this.Error = this.Build(theme.Palette.Error);
        this.Attention = this.Build(theme.Palette.Warn);
        this.Overridden = this.Build(theme.Palette.Dim);
        this.Accent = this.Build(theme.Palette.Accent);
        this.Dim = this.Build(theme.Palette.Dim);
    }

    /// <summary>Default row content — a name, a description.</summary>
    public TgScheme Normal { get; }

    /// <summary>Connected, running, healthy.</summary>
    public TgScheme Healthy { get; }

    /// <summary>Enabled but idle or disconnected.</summary>
    public TgScheme Idle { get; }

    /// <summary>Switched off by configuration.</summary>
    public TgScheme Disabled { get; }

    /// <summary>Failed.</summary>
    public TgScheme Error { get; }

    /// <summary>Awaiting a decision from the user.</summary>
    public TgScheme Attention { get; }

    /// <summary>Shadowed by a higher-precedence entry.</summary>
    public TgScheme Overridden { get; }

    /// <summary>Type and transport tags, which must read as distinct without implying an outcome.</summary>
    public TgScheme Accent { get; }

    /// <summary>Secondary metadata that should recede behind the name column.</summary>
    public TgScheme Dim { get; }

    /// <summary>The scheme for <paramref name="state"/>.</summary>
    public TgScheme For(BrowserItemState state) => state switch
    {
        BrowserItemState.Healthy => this.Healthy,
        BrowserItemState.Idle => this.Idle,
        BrowserItemState.Disabled => this.Disabled,
        BrowserItemState.Error => this.Error,
        BrowserItemState.Attention => this.Attention,
        BrowserItemState.Overridden => this.Overridden,
        _ => this.Normal,
    };

    /// <summary>
    /// The scheme for a whole row. Disabled and overridden rows recede wholesale so an inactive
    /// entry reads as inactive at a glance rather than through a word in a column.
    /// </summary>
    public TgScheme ForRow(BrowserItemState state) => state switch
    {
        BrowserItemState.Disabled => this.Disabled,
        BrowserItemState.Overridden => this.Overridden,
        _ => this.Normal,
    };

    private TgScheme Build(TuiThemeColor foreground)
    {
        var normal = this.theme.Attribute(foreground, this.theme.Background, this.driver);
        var focus = this.theme.Attribute(this.theme.SelectionText, this.theme.SelectionBackground, this.driver);
        return Solid(normal, focus);
    }

    private static TgScheme Solid(TgAttribute normal, TgAttribute focus) => new()
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
