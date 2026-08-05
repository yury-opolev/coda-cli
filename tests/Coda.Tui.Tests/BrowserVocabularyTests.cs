using Coda.Tui.Ui.Rendering;
using Terminal.Gui.Drivers;

namespace Coda.Tui.Tests;

/// <summary>
/// Covers the shared browser vocabulary (Task 3 of the TUI browser UX plan): the status glyph set
/// and the per-state schemes every browser resolves colour through.
/// </summary>
public sealed class BrowserVocabularyTests
{
    private static readonly BrowserItemState[] AllStates =
        Enum.GetValues<BrowserItemState>();

    // -------------------------------------------------------------------------
    // StatusGlyphs
    // -------------------------------------------------------------------------

    [Fact]
    public void For_selects_the_unicode_or_ascii_set()
    {
        Assert.Same(StatusGlyphs.Unicode, StatusGlyphs.For(unicodeOutput: true));
        Assert.Same(StatusGlyphs.Ascii, StatusGlyphs.For(unicodeOutput: false));
    }

    /// <summary>
    /// A status column only stays aligned if every glyph occupies exactly one cell. Several
    /// plausible candidates are ambiguous-width, so this is enforced rather than assumed.
    /// </summary>
    [Fact]
    public void Glyphs_are_one_cell_wide_in_both_sets()
    {
        foreach (var state in AllStates)
        {
            Assert.Equal(1, TerminalCellText.Width(StatusGlyphs.Unicode[state]));
            Assert.Equal(1, TerminalCellText.Width(StatusGlyphs.Ascii[state]));
        }
    }

    [Fact]
    public void Ascii_glyphs_contain_no_characters_outside_ascii()
    {
        foreach (var state in AllStates)
        {
            Assert.All(StatusGlyphs.Ascii[state], ch => Assert.InRange(ch, (char)0x20, (char)0x7e));
        }
    }

    [Fact]
    public void Every_state_maps_to_a_glyph_and_states_are_distinguishable()
    {
        foreach (var state in AllStates)
        {
            Assert.False(string.IsNullOrEmpty(StatusGlyphs.Unicode[state]));
            Assert.False(string.IsNullOrEmpty(StatusGlyphs.Ascii[state]));
        }

        // Healthy, idle, disabled and error must never be confusable — they are the four a user
        // scans for. Attention and overridden may share a glyph with another set member in ASCII,
        // where the character repertoire runs out, because they are also colour-differentiated.
        var distinct = new[]
        {
            StatusGlyphs.Unicode[BrowserItemState.Healthy],
            StatusGlyphs.Unicode[BrowserItemState.Idle],
            StatusGlyphs.Unicode[BrowserItemState.Disabled],
            StatusGlyphs.Unicode[BrowserItemState.Error],
        };

        Assert.Equal(distinct.Length, distinct.Distinct(StringComparer.Ordinal).Count());
    }

    // -------------------------------------------------------------------------
    // BrowserSchemes
    // -------------------------------------------------------------------------

    [Fact]
    public void Schemes_resolve_for_every_state_without_a_driver()
    {
        var schemes = new BrowserSchemes(TuiTheme.WarmEmber, driver: null);

        foreach (var state in AllStates)
        {
            Assert.NotNull(schemes.For(state));
            Assert.NotNull(schemes.ForRow(state));
        }
    }

    [Fact]
    public void Outcome_states_resolve_to_distinct_foregrounds()
    {
        var schemes = new BrowserSchemes(TuiTheme.WarmEmber, driver: null);

        var healthy = schemes.For(BrowserItemState.Healthy).Normal.Foreground;
        var error = schemes.For(BrowserItemState.Error).Normal.Foreground;
        var attention = schemes.For(BrowserItemState.Attention).Normal.Foreground;
        var dim = schemes.For(BrowserItemState.Idle).Normal.Foreground;

        Assert.NotEqual(healthy, error);
        Assert.NotEqual(healthy, attention);
        Assert.NotEqual(error, attention);
        Assert.NotEqual(healthy, dim);
    }

    /// <summary>
    /// Disabled and overridden rows recede wholesale; anything else keeps the normal row colour so
    /// a single failing server does not repaint the entire list.
    /// </summary>
    [Fact]
    public void Row_scheme_dims_only_disabled_and_overridden()
    {
        var schemes = new BrowserSchemes(TuiTheme.WarmEmber, driver: null);
        var normal = schemes.Normal.Normal.Foreground;
        var dim = schemes.Dim.Normal.Foreground;

        Assert.Equal(dim, schemes.ForRow(BrowserItemState.Disabled).Normal.Foreground);
        Assert.Equal(dim, schemes.ForRow(BrowserItemState.Overridden).Normal.Foreground);
        Assert.Equal(normal, schemes.ForRow(BrowserItemState.Healthy).Normal.Foreground);
        Assert.Equal(normal, schemes.ForRow(BrowserItemState.Error).Normal.Foreground);
    }

    [Fact]
    public void Schemes_use_the_selection_colours_for_focus()
    {
        var theme = TuiTheme.WarmEmber;
        var schemes = new BrowserSchemes(theme, driver: null);

        var focus = schemes.Normal.Focus;

        Assert.Equal(TuiTheme.Resolve(theme.SelectionText, trueColor: false), focus.Foreground);
        Assert.Equal(TuiTheme.Resolve(theme.SelectionBackground, trueColor: false), focus.Background);
    }

    /// <summary>
    /// A null driver reports no true-colour support, so roles resolve through their named 16-colour
    /// fallbacks. Proving both paths resolve is what keeps the vocabulary usable on a low-colour
    /// terminal.
    /// </summary>
    [Fact]
    public void Schemes_resolve_through_the_sixteen_colour_fallback_when_true_colour_is_unavailable()
    {
        var theme = TuiTheme.WarmEmber;
        var schemes = new BrowserSchemes(theme, driver: null);

        Assert.Equal(
            TuiTheme.Resolve(theme.Palette.Success, trueColor: false),
            schemes.Healthy.Normal.Foreground);
        Assert.Equal(
            TuiTheme.Resolve(theme.Palette.Error, trueColor: false),
            schemes.Error.Normal.Foreground);
    }

    [Fact]
    public void Schemes_follow_a_retinted_theme_rather_than_hard_coding_colour()
    {
        var retinted = new TuiTheme
        {
            Palette = TuiPalette.WarmEmber with
            {
                Success = new(new Terminal.Gui.Drawing.Color(1, 2, 3), Terminal.Gui.Drawing.ColorName16.Green),
            },
        };

        var schemes = new BrowserSchemes(retinted, driver: null);

        Assert.Equal(
            TuiTheme.Resolve(retinted.Palette.Success, trueColor: false),
            schemes.Healthy.Normal.Foreground);
    }

}
