using Spectre.Console;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Rendering;

public static class Theme
{
    public static string Accent => CodaThemes.Current.Console.Accent;
    public static string Dim => CodaThemes.Current.Console.Dim;
    public static Color AccentColor => ParseColor(Accent);

    public static string AccentMarkup(string text) => $"[{Accent}]{Markup.Escape(text)}[/]";
    public static string DimMarkup(string text) => $"[{Dim}]{Markup.Escape(text)}[/]";
    public static string BoldMarkup(string text) => $"[bold]{Markup.Escape(text)}[/]";
    public static string SuccessMarkup(string text) => $"[{CodaThemes.Current.Console.Success}]{Markup.Escape(text)}[/]";
    public static string WarnMarkup(string text) => $"[{CodaThemes.Current.Console.Warn}]{Markup.Escape(text)}[/]";
    public static string ErrorMarkup(string text) => $"[{CodaThemes.Current.Console.Error}]{Markup.Escape(text)}[/]";
    public static string PromptGlyph => $"[{Accent}]›[/]";

    private static Color ParseColor(string value)
    {
        if (value.Length == 7 && value[0] == '#')
        {
            return new Color(
                Convert.ToByte(value[1..3], 16),
                Convert.ToByte(value[3..5], 16),
                Convert.ToByte(value[5..7], 16));
        }

        return Color.DeepSkyBlue1;
    }
}
