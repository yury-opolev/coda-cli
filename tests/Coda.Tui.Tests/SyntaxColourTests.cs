using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

/// <summary>
/// Verifies that every built-in theme gives each syntax token kind a distinct, palette-routed colour.
/// Routing through the palette (rather than per-theme literals) is what keeps <c>RoleParityTests</c>
/// satisfied automatically: each theme already ships a distinct palette, so the derived syntax roles
/// differ between themes without any theme having to restate them.
/// </summary>
public sealed class SyntaxColourTests
{
    public static TheoryData<string> BuiltInThemeNames() => new() { "default", "warm-ember", "cool-dark" };

    [Theory]
    [MemberData(nameof(BuiltInThemeNames))]
    public void Syntax_roles_resolve_through_the_theme_palette(string themeName)
    {
        Assert.True(CodaThemes.TryGet(themeName, out var theme));
        var palette = theme.Tui.Palette;

        Assert.Equal(palette.Accent, theme.Tui.SyntaxKeyword);
        Assert.Equal(palette.Warn, theme.Tui.SyntaxType);
        Assert.Equal(palette.Success, theme.Tui.SyntaxString);
        Assert.Equal(palette.Error, theme.Tui.SyntaxNumber);
        Assert.Equal(palette.Dim, theme.Tui.SyntaxComment);
    }

    [Theory]
    [MemberData(nameof(BuiltInThemeNames))]
    public void Every_highlighted_token_kind_gets_a_distinct_colour(string themeName)
    {
        Assert.True(CodaThemes.TryGet(themeName, out var theme));

        var colours = new[]
        {
            theme.Tui.SyntaxKeyword.TrueColor,
            theme.Tui.SyntaxType.TrueColor,
            theme.Tui.SyntaxString.TrueColor,
            theme.Tui.SyntaxNumber.TrueColor,
            theme.Tui.SyntaxComment.TrueColor,
        };

        Assert.Equal(colours.Length, colours.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(BuiltInThemeNames))]
    public void The_accent_hue_is_distinct_from_every_other_palette_hue(string themeName)
    {
        Assert.True(CodaThemes.TryGet(themeName, out var theme));
        var palette = theme.Tui.Palette;

        var hues = new[]
        {
            palette.Accent.TrueColor,
            palette.Success.TrueColor,
            palette.Warn.TrueColor,
            palette.Error.TrueColor,
            palette.Dim.TrueColor,
        };

        Assert.Equal(hues.Length, hues.Distinct().Count());
    }
}
