using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using TgColor = Terminal.Gui.Drawing.Color;
using TgName = Terminal.Gui.Drawing.ColorName16;

namespace Coda.Tui.Tests;

/// <summary>
/// Host-neutral tests for the Warm Ember semantic theme: they lock the exact RGB values and named
/// 16-color fallbacks for every role, verify <see cref="TuiTheme.Resolve"/> picks RGB on true-color
/// drivers and the named value otherwise, and confirm the retained transcript view resolves each
/// <see cref="TranscriptRole"/> through the theme (including the forced-16-color driver path).
/// </summary>
public sealed class TuiThemeTests
{
    [Fact]
    public void Warm_ember_exposes_exact_semantic_rgb_and_named_fallbacks()
    {
        var theme = TuiTheme.WarmEmber;

        Assert.Equal(new TgColor(242, 214, 179), theme.TranscriptAssistant.TrueColor);
        Assert.Equal(new TgColor(230, 168, 74), theme.TranscriptUser.TrueColor);
        Assert.Equal(new TgColor(215, 168, 75), theme.TranscriptTool.TrueColor);
        Assert.Equal(new TgColor(233, 130, 107), theme.PermissionApproval.TrueColor);
        Assert.Equal(new TgColor(240, 199, 94), theme.Question.TrueColor);
        Assert.Equal(new TgColor(217, 104, 93), theme.Error.TrueColor);

        Assert.Equal(TgName.White, theme.TranscriptAssistant.Fallback);
        Assert.Equal(TgName.BrightYellow, theme.TranscriptUser.Fallback);
        Assert.Equal(TgName.BrightYellow, theme.TranscriptTool.Fallback);
        Assert.Equal(TgName.BrightRed, theme.PermissionApproval.Fallback);
        Assert.Equal(TgName.BrightYellow, theme.Question.Fallback);
        Assert.Equal(TgName.Red, theme.Error.Fallback);
        Assert.NotEqual(TgName.Blue, theme.TranscriptTool.Fallback);
        Assert.NotEqual(TgName.Magenta, theme.PermissionApproval.Fallback);
    }

    [Fact]
    public void Resolve_uses_rgb_for_truecolor_and_named_value_for_low_color()
    {
        var role = TuiTheme.WarmEmber.TranscriptTool;

        Assert.Equal(role.TrueColor, TuiTheme.Resolve(role, trueColor: true));
        Assert.Equal(new TgColor(role.Fallback), TuiTheme.Resolve(role, trueColor: false));
    }

    [Theory]
    [InlineData(TranscriptRole.Assistant, 242, 214, 179)]
    [InlineData(TranscriptRole.User, 230, 168, 74)]
    [InlineData(TranscriptRole.Tool, 215, 168, 75)]
    [InlineData(TranscriptRole.Permission, 233, 130, 107)]
    [InlineData(TranscriptRole.Question, 240, 199, 94)]
    [InlineData(TranscriptRole.Warning, 240, 199, 94)]
    [InlineData(TranscriptRole.Error, 217, 104, 93)]
    public void Transcript_roles_resolve_through_theme(
        TranscriptRole role,
        int red,
        int green,
        int blue)
    {
        using IApplication app = Application.Create();
        using var view = new VirtualizedTranscriptView(app, theme: TuiTheme.WarmEmber);

        var color = view.AttributeFor(role, trueColor: true).Foreground;

        Assert.Equal(new TgColor(red, green, blue), color);
    }

    [Fact]
    public void Forced_16_color_driver_uses_named_tool_and_approval_fallbacks()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.Force16Colors = true;
        using var view = new VirtualizedTranscriptView(app, theme: TuiTheme.WarmEmber);

        Assert.Equal(
            new TgColor(TgName.BrightYellow),
            view.AttributeFor(TranscriptRole.Tool).Foreground);
        Assert.Equal(
            new TgColor(TgName.BrightRed),
            view.AttributeFor(TranscriptRole.Permission).Foreground);
    }

    [Fact]
    public void Surface_scheme_uses_neutral_warm_foreground_over_background_for_every_state()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);

        var scheme = TuiTheme.WarmEmber.SurfaceScheme(app.Driver);

        var background = TuiTheme.WarmEmber.Background.TrueColor;
        var foreground = TuiTheme.WarmEmber.TranscriptAssistant.TrueColor;

        // The neutral warm foreground reads over the Warm Ember background...
        Assert.Equal(foreground, scheme.Normal.Foreground);

        // ...and every scheme state paints the same uniform background, so no inherited surface can
        // introduce a different backdrop regardless of focus/active/disabled state.
        foreach (var attribute in new[]
        {
            scheme.Normal, scheme.HotNormal, scheme.Focus, scheme.HotFocus, scheme.Active,
            scheme.HotActive, scheme.Highlight, scheme.Editable, scheme.ReadOnly, scheme.Disabled,
        })
        {
            Assert.Equal(background, attribute.Background);
        }
    }

    [Fact]
    public void Surface_scheme_falls_back_to_named_background_when_forced_to_16_colors()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.Force16Colors = true;

        var scheme = TuiTheme.WarmEmber.SurfaceScheme(app.Driver);

        Assert.Equal(new TgColor(TuiTheme.WarmEmber.Background.Fallback), scheme.Normal.Background);
        Assert.Equal(new TgColor(TuiTheme.WarmEmber.TranscriptAssistant.Fallback), scheme.Normal.Foreground);
    }
}
