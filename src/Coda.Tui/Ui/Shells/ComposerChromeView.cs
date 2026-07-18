using System.Text;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;
using TgScheme = Terminal.Gui.Drawing.Scheme;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// The borderless decor drawn beneath and to the left of the composer. It fills the composer region with a
/// subtle dark background, paints a colored vertical accent bar down its left edge, and shows either a
/// <c>&gt;</c> prompt glyph (ready) or an accent-colored <c>Initializing…</c> label (while the semantic
/// startup operation is active). It never takes focus and never draws box-drawing border characters, so the
/// composer reads as a flat, accented input rather than a bordered box.
/// </summary>
/// <remarks>
/// Colors are expressed through fully-qualified <see cref="Terminal.Gui.Drawing"/> types (aliased here as
/// <c>TgColor</c>/<c>TgAttribute</c>) so the global <c>Color = Spectre.Console.Color</c> alias never leaks
/// in. The accent is a named 16-color so it degrades cleanly on low-color terminals; the dark panel is a
/// near-black RGB that quantizes to the terminal's own background there. Rendering is also exposed as plain
/// rows through <see cref="RenderRows"/> so the shell (and tests) can inspect it without a live draw.
/// </remarks>
internal sealed class ComposerChromeView : View
{
    /// <summary>The vertical accent bar glyph drawn in the first column of every row.</summary>
    internal const string AccentGlyph = "▌";

    /// <summary>The ready-state prompt glyph marking where input begins.</summary>
    internal const string PromptGlyph = ">";

    /// <summary>The label shown in place of the prompt glyph while startup is active.</summary>
    internal const string InitializingText = "Initializing…";

    /// <summary>The column at which the prompt glyph / initializing label starts (after the accent + gap).</summary>
    private const int LabelColumn = 2;

    private static readonly TgColor PanelBackground = new(30, 30, 34);
    private static readonly TgColor PanelForeground = new(210, 210, 210);
    private static readonly TgColor PromptForeground = new(235, 235, 235);
    private static readonly TgColor AccentColor = new(Terminal.Gui.Drawing.ColorName16.BrightCyan);
    private static readonly Rune AccentRune = new(AccentGlyph[0]);

    private bool ready = true;

    public ComposerChromeView()
    {
        this.CanFocus = false;
    }

    /// <summary>Whether the composer is ready for input; false while the startup operation is active.</summary>
    internal bool Ready => this.ready;

    /// <summary>The text drawn at the input start: the prompt glyph when ready, otherwise the startup label.</summary>
    internal string DisplayText => this.ready ? PromptGlyph : InitializingText;

    /// <summary>
    /// A shared dark input scheme so the composer's own editing surface matches the chrome's panel
    /// background across the whole composer region, keeping every role (normal, focus, read-only, ...)
    /// on the same subtle-dark background.
    /// </summary>
    internal static TgScheme CreateInputScheme()
    {
        var normal = new TgAttribute(PanelForeground, PanelBackground);
        var focus = new TgAttribute(PromptForeground, PanelBackground);
        return new TgScheme
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
    /// The plain-text rows the chrome paints for a region of the given size: each row begins with the
    /// accent glyph, the first row carries the prompt glyph / initializing label, and every row is exactly
    /// <paramref name="width"/> cells so a narrow terminal can never overflow. Exposed for the shell/tests.
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
            buffer[0] = AccentGlyph[0];

            if (row == 0)
            {
                var label = this.DisplayText;
                for (var i = 0; i < label.Length && LabelColumn + i < width; i++)
                {
                    buffer[LabelColumn + i] = label[i];
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

        var background = new TgAttribute(PanelForeground, PanelBackground);
        var accent = new TgAttribute(AccentColor, PanelBackground);
        var label = new TgAttribute(this.ready ? PromptForeground : AccentColor, PanelBackground);

        var blank = new string(' ', width);
        for (var row = 0; row < height; row++)
        {
            this.SetAttribute(background);
            this.Move(0, row);
            this.AddStr(blank);

            this.SetAttribute(accent);
            this.Move(0, row);
            this.AddRune(AccentRune);
        }

        if (LabelColumn < width)
        {
            this.SetAttribute(label);
            this.Move(LabelColumn, 0);
            this.AddStr(Fit(this.DisplayText, width - LabelColumn));
        }

        return true;
    }

    private static string Fit(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        return text.Length <= width ? text : text[..width];
    }
}
