using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

/// <summary>
/// Host-neutral tests for <see cref="ComposerChromeView"/>: the borderless decor beneath the composer.
/// When ready it fills the region with the theme's dark background and draws only a warm <c>&gt;</c>
/// prompt in column 0; during startup it stays dark and draws no chrome text (the operational status row
/// owns the <c>Initializing</c> message). Rendering is exposed through
/// <see cref="ComposerChromeView.RenderRows"/> so these assertions need no running application.
/// </summary>
public sealed class ComposerChromeViewTests
{
    private static readonly char[] BorderCharacters =
    [
        '─', '│', '┌', '┐', '└', '┘', '├', '┤', '┬', '┴', '┼',
        '═', '║', '╔', '╗', '╚', '╝', '╭', '╮', '╯', '╰',
    ];

    [Fact]
    public void Non_focusable_and_ready_by_default()
    {
        using var chrome = new ComposerChromeView();

        Assert.False(chrome.CanFocus);
        Assert.True(chrome.Ready);
        Assert.Equal(ComposerChromeView.PromptGlyph, chrome.DisplayText);
    }

    [Fact]
    public void Ready_state_draws_the_warm_prompt_with_subtle_half_block_edge_shading()
    {
        using var chrome = new ComposerChromeView(TuiTheme.WarmEmber);

        var rows = chrome.RenderRows(width: 12, height: 4);

        // A full-width upper-half-block top edge, the warm prompt at column 0 of the first content row (so
        // the composer's own rows overdraw everything but the gutter), an interior content row, and a
        // full-width lower-half-block bottom edge — no vertical accent bar or box border.
        Assert.Equal(["▀▀▀▀▀▀▀▀▀▀▀▀", ">           ", "            ", "▄▄▄▄▄▄▄▄▄▄▄▄"], rows);
        Assert.DoesNotContain(rows, row => row.Contains('▌'));
        Assert.DoesNotContain(rows, row => row.Contains('│'));
        Assert.DoesNotContain(rows, row => row.Contains("Initializing", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_state_keeps_the_dark_region_but_draws_no_chrome_text()
    {
        using var chrome = new ComposerChromeView(TuiTheme.WarmEmber);

        chrome.SetReady(false);

        // During startup the panel stays dark and blank — no prompt and no edge shading.
        Assert.Equal(string.Empty, chrome.DisplayText);
        Assert.Equal(["            ", "            ", "            "], chrome.RenderRows(12, 3));
    }

    [Fact]
    public void Never_renders_box_drawing_border_characters()
    {
        using var chrome = new ComposerChromeView();

        AssertNoBorders(chrome.RenderRows(width: 40, height: 3));

        chrome.SetReady(false);
        AssertNoBorders(chrome.RenderRows(width: 40, height: 3));
    }

    [Fact]
    public void Narrow_width_truncates_without_overflow()
    {
        using var chrome = new ComposerChromeView();

        var rows = chrome.RenderRows(width: 6, height: 2);

        Assert.All(rows, row => Assert.True(row.Length <= 6, $"row '{row}' exceeds width 6"));
    }

    private static void AssertNoBorders(IReadOnlyList<string> rows) =>
        Assert.All(rows, row => Assert.False(
            row.Any(character => Array.IndexOf(BorderCharacters, character) >= 0),
            $"row '{row}' contains a box-drawing border character"));
}
