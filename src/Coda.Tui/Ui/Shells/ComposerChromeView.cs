using System.Text;
using Coda.Tui.Ui.Rendering;
using Terminal.Gui.Drivers;
using TgScheme = Terminal.Gui.Drawing.Scheme;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// The borderless decor drawn around the composer. It fills the region with the Warm Ember theme's distinct
/// <see cref="TuiTheme.ComposerPanelBackground"/> (a touch lighter than the shell surface) and, when the
/// composer is ready for input, draws subtle half-block edge shading — a full-width upper-half-block
/// (<c>▀</c>) top row and lower-half-block (<c>▄</c>) bottom row that frame the composer content rows — plus
/// a single warm <c>&gt;</c> prompt glyph in column 0 of the first content row (the row just below the top
/// edge, aligned with the composer's own first line). It never takes focus, never draws box-drawing border
/// characters or a vertical accent bar, and never owns any status text: during startup it simply stays dark
/// and blank while the operational status row owns the <c>Initializing</c> message.
/// </summary>
/// <remarks>
/// Colors come entirely from <see cref="TuiTheme"/> so this view carries no independent palette. Rendering
/// is also exposed as plain rows through <see cref="RenderRows"/> so the shell (and tests) can inspect it
/// without a live draw.
/// </remarks>
internal sealed class ComposerChromeView : View
{
    /// <summary>The ready-state prompt glyph marking where input begins.</summary>
    internal const string PromptGlyph = ">";

    /// <summary>The upper-half-block drawn along the panel's top edge as subtle shading.</summary>
    internal const char TopEdgeGlyph = '▀';

    /// <summary>The lower-half-block drawn along the panel's bottom edge as subtle shading.</summary>
    internal const char BottomEdgeGlyph = '▄';

    /// <summary>The column at which the prompt glyph is drawn when ready.</summary>
    private const int PromptColumn = 0;

    /// <summary>
    /// The row carrying the prompt glyph when ready: the first content row (row 1), one below the top edge,
    /// so the composer's own rows sit directly over it and the warm <c>&gt;</c> shows in the gutter.
    /// </summary>
    private const int PromptRow = 1;

    private readonly TuiTheme theme;
    private bool ready = true;

    public ComposerChromeView(TuiTheme? theme = null)
    {
        this.theme = theme ?? TuiTheme.WarmEmber;
        this.CanFocus = false;
    }

    /// <summary>Whether the composer is ready for input; false while the startup operation is active.</summary>
    internal bool Ready => this.ready;

    /// <summary>The text drawn at the input start: the prompt glyph when ready, otherwise nothing.</summary>
    internal string DisplayText => this.ready ? PromptGlyph : string.Empty;

    /// <summary>
    /// A shared dark input scheme so the composer's own editing surface matches the chrome's background
    /// across the whole composer region, keyed to the driver's color depth.
    /// </summary>
    internal TgScheme CreateInputScheme(IDriver? driver) => this.theme.ComposerScheme(driver);

    /// <summary>Sets the readiness state and requests a redraw when it changes.</summary>
    internal void SetReady(bool value)
    {
        if (this.ready == value)
        {
            return;
        }

        this.ready = value;
        this.SetNeedsDraw();
    }

    /// <summary>
    /// The plain-text rows the chrome paints for a region of the given size. When ready, the first row is a
    /// full-width upper-half-block (<c>▀</c>) top edge, the second row (the first content row) carries the
    /// <c>&gt;</c> prompt glyph in column 0, interior rows are blank, and the last row is a full-width
    /// lower-half-block (<c>▄</c>) bottom edge. During startup every row is blank. No accent bar, vertical
    /// rule, or box border is ever drawn. Exposed for the shell/tests.
    /// </summary>
    internal IReadOnlyList<string> RenderRows(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return Array.Empty<string>();
        }

        var rows = new List<string>(height);
        for (var row = 0; row < height; row++)
        {
            var buffer = new char[width];
            Array.Fill(buffer, ' ');

            if (this.ready)
            {
                if (row == 0)
                {
                    Array.Fill(buffer, TopEdgeGlyph);
                }
                else if (row == height - 1)
                {
                    Array.Fill(buffer, BottomEdgeGlyph);
                }
                else if (row == PromptRow && PromptColumn < width)
                {
                    buffer[PromptColumn] = PromptGlyph[0];
                }
            }

            rows.Add(new string(buffer));
        }

        return rows;
    }

    /// <inheritdoc />
    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (context is not null)
        {
            this.ClearViewport(context);
        }

        var width = Math.Max(0, this.Viewport.Width);
        var height = Math.Max(0, this.Viewport.Height);
        if (width == 0 || height == 0)
        {
            return true;
        }

        var driver = this.App?.Driver;
        var background = this.theme.Attribute(this.theme.ComposerText, this.theme.ComposerPanelBackground, driver);

        var blank = new string(' ', width);
        for (var row = 0; row < height; row++)
        {
            this.SetAttribute(background);
            this.Move(0, row);
            this.AddStr(blank);
        }

        if (this.ready)
        {
            // Subtle half-block edge shading: an upper-half-block top row and lower-half-block bottom row in
            // a warm tone a touch lighter than the panel, so the seam between shell and panel reads soft.
            var edge = this.theme.Attribute(this.theme.ComposerPanelEdge, this.theme.ComposerPanelBackground, driver);
            this.SetAttribute(edge);
            this.Move(0, 0);
            this.AddStr(new string(ComposerChromeView.TopEdgeGlyph, width));
            if (height > 1)
            {
                this.Move(0, height - 1);
                this.AddStr(new string(ComposerChromeView.BottomEdgeGlyph, width));
            }

            // The warm '>' prompt sits on the first content row (one below the top edge) in the gutter, so
            // the composer's own rows overdraw everything but this glyph.
            if (PromptRow < height - 1 && PromptColumn < width)
            {
                var prompt = this.theme.Attribute(this.theme.ComposerPrompt, this.theme.ComposerPanelBackground, driver);
                this.SetAttribute(prompt);
                this.Move(PromptColumn, PromptRow);
                this.AddRune(new Rune(PromptGlyph[0]));
            }
        }

        return true;
    }
}
