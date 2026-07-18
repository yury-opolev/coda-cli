using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

/// <summary>
/// Host-neutral tests for <see cref="ComposerChromeView"/>: the borderless decor that draws the
/// composer's subtle dark background, its left accent bar, and either a <c>&gt;</c> prompt glyph (ready)
/// or an <c>Initializing…</c> label (while startup is active). Rendering is exposed through
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
    public void Ready_state_renders_accent_bar_and_prompt_glyph()
    {
        using var chrome = new ComposerChromeView();

        var rows = chrome.RenderRows(width: 40, height: 3);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.StartsWith(ComposerChromeView.AccentGlyph, row));
        Assert.Contains(ComposerChromeView.PromptGlyph, rows[0]);
        Assert.DoesNotContain("Initializing", rows[0]);
    }

    [Fact]
    public void Startup_state_renders_initializing_label_instead_of_prompt()
    {
        using var chrome = new ComposerChromeView();

        chrome.SetReady(false);

        Assert.False(chrome.Ready);
        Assert.Equal(ComposerChromeView.InitializingText, chrome.DisplayText);

        var rows = chrome.RenderRows(width: 40, height: 3);
        Assert.All(rows, row => Assert.StartsWith(ComposerChromeView.AccentGlyph, row));
        Assert.Contains(ComposerChromeView.InitializingText, rows[0]);
        Assert.DoesNotContain(ComposerChromeView.PromptGlyph, rows[0]);
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
        chrome.SetReady(false);

        var rows = chrome.RenderRows(width: 6, height: 2);

        Assert.All(rows, row => Assert.True(row.Length <= 6, $"row '{row}' exceeds width 6"));
        Assert.All(rows, row => Assert.StartsWith(ComposerChromeView.AccentGlyph, row));
    }

    private static void AssertNoBorders(IReadOnlyList<string> rows) =>
        Assert.All(rows, row => Assert.False(
            row.Any(character => Array.IndexOf(BorderCharacters, character) >= 0),
            $"row '{row}' contains a box-drawing border character"));
}
