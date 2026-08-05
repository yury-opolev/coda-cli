using Coda.Tui.Ui.Rendering;
using Terminal.Gui.Drivers;
using TgScheme = Terminal.Gui.Drawing.Scheme;

namespace Coda.Tui.Ui.Shells;

internal sealed class ComposerChromeView : View
{
    internal const string PromptGlyph = "\u276f";

    /// <summary>
    /// The top edge is a <em>lower</em> half block: the cell's upper half keeps the shell background
    /// and its lower half carries the panel colour, so the panel appears to begin half a row above
    /// the first content row instead of starting abruptly at a cell boundary.
    /// </summary>
    internal const char TopEdgeGlyph = '▄';

    /// <summary>
    /// The bottom edge mirrors <see cref="TopEdgeGlyph"/> with an <em>upper</em> half block, so the
    /// panel appears to end half a row below the last content row.
    /// </summary>
    internal const char BottomEdgeGlyph = '▀';

    private const int PromptColumn = 0;
    private const int PromptRow = 1;

    private TuiTheme theme;
    private bool ready = true;

    public ComposerChromeView(TuiTheme? theme = null)
    {
        this.theme = theme ?? CodaThemes.Current.Tui;
        this.CanFocus = false;
    }

    internal void ApplyTheme(TuiTheme theme)
    {
        this.theme = theme ?? throw new ArgumentNullException(nameof(theme));
        this.SetNeedsDraw();
    }

    internal bool Ready => this.ready;
    internal string DisplayText => this.ready ? PromptGlyph : string.Empty;
    internal TgScheme CreateInputScheme(IDriver? driver) => this.theme.ComposerScheme(driver);

    internal void SetReady(bool value)
    {
        if (this.ready == value)
        {
            return;
        }

        this.ready = value;
        this.SetNeedsDraw();
    }

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
            // The edge rows are painted against the SHELL background, not the panel background: the
            // half-block glyph then reads as the panel bleeding half a row outward, rather than as a
            // lighter rim floating inside the panel.
            var edge = this.theme.Attribute(this.theme.ComposerPanelEdge, this.theme.Background, driver);
            this.SetAttribute(edge);
            this.Move(0, 0);
            this.AddStr(new string(TopEdgeGlyph, width));
            if (height > 1)
            {
                this.Move(0, height - 1);
                this.AddStr(new string(BottomEdgeGlyph, width));
            }

            if (PromptRow < height - 1 && PromptColumn < width)
            {
                var prompt = this.theme.Attribute(this.theme.ComposerPrompt, this.theme.ComposerPanelBackground, driver);
                this.SetAttribute(prompt);
                this.Move(PromptColumn, PromptRow);
                this.AddStr(PromptGlyph);
            }
        }

        return true;
    }
}

