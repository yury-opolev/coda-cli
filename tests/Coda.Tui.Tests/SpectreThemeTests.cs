using Coda.Tui.Rendering;
using Coda.Tui.Ui.Rendering;
using SpectreColor = Spectre.Console.Color;

namespace Coda.Tui.Tests;

[Collection("ThemeState")]
public sealed class SpectreThemeTests : IDisposable
{
    private readonly CodaTheme originalTheme = CodaThemes.Current;

    public void Dispose() => CodaThemes.Set(this.originalTheme);

    [Fact]
    public void Accent_properties_follow_the_current_console_palette()
    {
        CodaThemes.Set(CodaThemes.Default);
        Assert.Equal("#82B4FF", Theme.Accent);
        Assert.Equal("#666F80", Theme.Dim);
        Assert.Equal(new SpectreColor(130, 180, 255), Theme.AccentColor);
        Assert.Equal("[#82B4FF]›[/]", Theme.PromptGlyph);

        CodaThemes.Set(CodaThemes.CoolDark);
        Assert.Equal("#00BEC8", Theme.Accent);
        Assert.Equal("#64829A", Theme.Dim);
        Assert.Equal(new SpectreColor(0, 190, 200), Theme.AccentColor);
        Assert.Equal("[#00BEC8]›[/]", Theme.PromptGlyph);
    }

    [Fact]
    public void Semantic_markup_uses_the_current_palette_and_escapes_text()
    {
        CodaThemes.Set(CodaThemes.WarmEmber);

        Assert.Equal("[#E6A84A]x[[y]][/]", Theme.AccentMarkup("x[y]"));
        Assert.Equal("[#6e6455]x[[y]][/]", Theme.DimMarkup("x[y]"));
        Assert.Equal("[#5C8C44]ok[[/]][/]", Theme.SuccessMarkup("ok[/]"));
        Assert.Equal("[#C88830]warn[[/]][/]", Theme.WarnMarkup("warn[/]"));
        Assert.Equal("[#D9685D]err[[/]][/]", Theme.ErrorMarkup("err[/]"));
    }
}


