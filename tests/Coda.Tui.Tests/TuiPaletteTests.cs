using Coda.Tui.Ui.Rendering;
using TgColor = Terminal.Gui.Drawing.Color;
using TgName = Terminal.Gui.Drawing.ColorName16;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for <see cref="TuiPalette"/>: the named base colour record that semantic roles resolve through.
/// Verifies re-tinting indirection, default inheritance, within-palette distinctness, and
/// the per-theme palette parity guard.
/// </summary>
public sealed class TuiPaletteTests
{
    // -----------------------------------------------------------------------
    // Re-tinting: changing Palette routes through to dependent roles
    // -----------------------------------------------------------------------

    [Fact]
    public void Retinting_caution_makes_Warning_ToolPartialFailure_and_PermissionApproved_report_that_colour()
    {
        var distinctive = new TuiThemeColor(new TgColor(99, 42, 17), TgName.BrightMagenta);
        var theme = new TuiTheme
        {
            Palette = new TuiPalette { Caution = distinctive },
        };

        // All three roles that resolve through Caution must report the new colour.
        Assert.Equal(distinctive, theme.Warning);
        Assert.Equal(distinctive, theme.ToolPartialFailure);
        Assert.Equal(distinctive, theme.PermissionApproved);

        // Roles that resolve through other palette entries must be unaffected.
        Assert.NotEqual(distinctive, theme.ToolSuccess);
        Assert.NotEqual(distinctive, theme.Error);
        Assert.NotEqual(distinctive, theme.Notification);
    }

    [Fact]
    public void Retinting_success_only_changes_ToolSuccess()
    {
        var distinctive = new TuiThemeColor(new TgColor(1, 200, 1), TgName.BrightGreen);
        var theme = new TuiTheme
        {
            Palette = new TuiPalette { Success = distinctive },
        };

        Assert.Equal(distinctive, theme.ToolSuccess);
        Assert.NotEqual(distinctive, theme.Warning);
        Assert.NotEqual(distinctive, theme.Error);
        Assert.NotEqual(distinctive, theme.Notification);
    }

    [Fact]
    public void Retinting_danger_only_changes_Error()
    {
        var distinctive = new TuiThemeColor(new TgColor(200, 10, 10), TgName.Red);
        var theme = new TuiTheme
        {
            Palette = new TuiPalette { Danger = distinctive },
        };

        Assert.Equal(distinctive, theme.Error);
        Assert.NotEqual(distinctive, theme.Warning);
        Assert.NotEqual(distinctive, theme.ToolSuccess);
        Assert.NotEqual(distinctive, theme.Notification);
    }

    [Fact]
    public void Retinting_info_only_changes_Notification()
    {
        var distinctive = new TuiThemeColor(new TgColor(100, 100, 200), TgName.Gray);
        var theme = new TuiTheme
        {
            Palette = new TuiPalette { Info = distinctive },
        };

        Assert.Equal(distinctive, theme.Notification);
        Assert.NotEqual(distinctive, theme.Warning);
        Assert.NotEqual(distinctive, theme.ToolSuccess);
        Assert.NotEqual(distinctive, theme.Error);
    }

    // -----------------------------------------------------------------------
    // Default Palette is TuiPalette.WarmEmber
    // -----------------------------------------------------------------------

    [Fact]
    public void Theme_omitting_Palette_inherits_TuiPalette_WarmEmber()
    {
        var theme = new TuiTheme();

        Assert.Equal(TuiPalette.WarmEmber.Success, theme.Palette.Success);
        Assert.Equal(TuiPalette.WarmEmber.Caution, theme.Palette.Caution);
        Assert.Equal(TuiPalette.WarmEmber.Danger, theme.Palette.Danger);
        Assert.Equal(TuiPalette.WarmEmber.Info, theme.Palette.Info);
    }

    [Fact]
    public void WarmEmber_theme_Palette_equals_TuiPalette_WarmEmber()
    {
        Assert.Equal(TuiPalette.WarmEmber, TuiTheme.WarmEmber.Palette);
    }

    // -----------------------------------------------------------------------
    // Within-palette distinctness: four entries stay distinguishable
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("default")]
    [InlineData("warm-ember")]
    [InlineData("cool-dark")]
    public void Each_built_in_theme_palette_has_four_pairwise_distinct_entries(string themeName)
    {
        Assert.True(CodaThemes.TryGet(themeName, out var theme));
        var palette = theme.Tui.Palette;

        var entries = new[] { palette.Success, palette.Caution, palette.Danger, palette.Info };

        // All four true-color values must be distinct.
        Assert.Equal(4, entries.Select(e => e.TrueColor).Distinct().Count());

        // The four named fallbacks must also be distinct.
        Assert.Equal(4, entries.Select(e => e.Fallback).Distinct().Count());
    }

    // -----------------------------------------------------------------------
    // Non-WarmEmber palette parity: each built-in theme's palette differs
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("default")]
    [InlineData("cool-dark")]
    public void Non_WarmEmber_built_in_theme_Palette_differs_from_TuiPalette_WarmEmber(string themeName)
    {
        Assert.True(CodaThemes.TryGet(themeName, out var theme));

        Assert.NotEqual(TuiPalette.WarmEmber, theme.Tui.Palette);
    }

    // -----------------------------------------------------------------------
    // WarmEmber palette entries are the expected named Warm Ember values
    // -----------------------------------------------------------------------

    [Fact]
    public void TuiPalette_WarmEmber_has_expected_success_colour()
    {
        Assert.Equal(new TgColor(110, 180, 85), TuiPalette.WarmEmber.Success.TrueColor);
        Assert.Equal(TgName.BrightGreen, TuiPalette.WarmEmber.Success.Fallback);
    }

    [Fact]
    public void TuiPalette_WarmEmber_has_expected_caution_colour()
    {
        Assert.Equal(new TgColor(240, 199, 94), TuiPalette.WarmEmber.Caution.TrueColor);
        Assert.Equal(TgName.Yellow, TuiPalette.WarmEmber.Caution.Fallback);
    }

    [Fact]
    public void TuiPalette_WarmEmber_has_expected_danger_colour()
    {
        Assert.Equal(new TgColor(217, 104, 93), TuiPalette.WarmEmber.Danger.TrueColor);
        Assert.Equal(TgName.Red, TuiPalette.WarmEmber.Danger.Fallback);
    }

    [Fact]
    public void TuiPalette_WarmEmber_has_expected_info_colour()
    {
        Assert.Equal(new TgColor(191, 174, 156), TuiPalette.WarmEmber.Info.TrueColor);
        Assert.Equal(TgName.Gray, TuiPalette.WarmEmber.Info.Fallback);
    }
}
