using System.Text;
using Coda.Tui.Ui.Rendering;
using Terminal.Gui.Drivers;
using TgScheme = Terminal.Gui.Drawing.Scheme;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// The borderless decor drawn beneath the composer. It fills the composer region with the Warm Ember
/// theme's near-black background and, when the composer is ready for input, draws a single warm
/// <c>&gt;</c> prompt glyph in column 0. It never takes focus, never draws box-drawing border
/// characters, and never owns any status text: during startup it simply stays dark and blank while the
/// operational status row owns the <c>Initializing</c> message.
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

    /// <summary>The column at which the prompt glyph is drawn when ready.</summary>
    private const int PromptColumn = 0;

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
    /// The plain-text rows the chrome paints for a region of the given size: every row is exactly
    /// <paramref name="width"/> spaces, and the first row carries the <c>&gt;</c> prompt glyph in column 0
    /// only when ready. No accent bar and no startup label are ever drawn. Exposed for the shell/tests.
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

            if (row == 0 && this.ready && PromptColumn < width)
            {
                buffer[PromptColumn] = PromptGlyph[0];
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
        var background = this.theme.Attribute(this.theme.ComposerText, this.theme.Background, driver);

        var blank = new string(' ', width);
        for (var row = 0; row < height; row++)
        {
            this.SetAttribute(background);
            this.Move(0, row);
            this.AddStr(blank);
        }

        if (this.ready && PromptColumn < width)
        {
            var prompt = this.theme.Attribute(this.theme.ComposerPrompt, this.theme.Background, driver);
            this.SetAttribute(prompt);
            this.Move(PromptColumn, 0);
            this.AddRune(new Rune(PromptGlyph[0]));
        }

        return true;
    }
}
