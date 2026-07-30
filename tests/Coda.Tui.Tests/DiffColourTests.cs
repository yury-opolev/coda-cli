using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

/// <summary>
/// Verifies that every built-in theme defines the new diff roles with meaningful, distinct colours.
/// DiffAdded must differ from DiffRemoved (green vs. red) and the matching background roles must
/// similarly differ, so the full-width coloured rows are visually unambiguous in every theme.
/// </summary>
public sealed class DiffColourTests
{
    public static TheoryData<string> BuiltInThemeNames() => new() { "default", "warm-ember", "cool-dark" };

    // -----------------------------------------------------------------------
    // DiffAdded and DiffRemoved foregrounds are distinct in every theme
    // -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BuiltInThemeNames))]
    public void DiffAdded_foreground_is_distinct_from_DiffRemoved_in_all_built_in_themes(string themeName)
    {
        Assert.True(CodaThemes.TryGet(themeName, out var theme));

        Assert.NotEqual(theme.Tui.DiffAdded.TrueColor, theme.Tui.DiffRemoved.TrueColor);
    }

    // -----------------------------------------------------------------------
    // DiffAddedBackground and DiffRemovedBackground are distinct in every theme
    // -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BuiltInThemeNames))]
    public void DiffAddedBackground_is_distinct_from_DiffRemovedBackground_in_all_built_in_themes(string themeName)
    {
        Assert.True(CodaThemes.TryGet(themeName, out var theme));

        Assert.NotEqual(theme.Tui.DiffAddedBackground.TrueColor, theme.Tui.DiffRemovedBackground.TrueColor);
    }

    // -----------------------------------------------------------------------
    // Palette routing: DiffAdded/DiffRemoved/DiffContext resolve through palette
    // -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BuiltInThemeNames))]
    public void Diff_roles_resolve_through_the_theme_palette(string themeName)
    {
        Assert.True(CodaThemes.TryGet(themeName, out var theme));
        var palette = theme.Tui.Palette;

        Assert.Equal(palette.Success, theme.Tui.DiffAdded);
        Assert.Equal(palette.Error, theme.Tui.DiffRemoved);
        Assert.Equal(palette.Dim, theme.Tui.DiffContext);
    }
}
