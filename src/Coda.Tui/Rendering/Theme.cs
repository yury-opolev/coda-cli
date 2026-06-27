using Spectre.Console;

namespace Coda.Tui.Rendering;

/// <summary>Centralized colours/markup helpers derived from <see cref="Branding"/>.</summary>
public static class Theme
{
    public const string Accent = Branding.AccentColor;
    public const string Dim = Branding.DimColor;

    /// <summary>The accent as a Spectre <see cref="Color"/> (keep in sync with <see cref="Accent"/>).</summary>
    public static readonly Color AccentColor = Color.DeepSkyBlue1;

    public static string AccentMarkup(string text) => $"[{Accent}]{Markup.Escape(text)}[/]";

    public static string DimMarkup(string text) => $"[{Dim}]{Markup.Escape(text)}[/]";

    public static string BoldMarkup(string text) => $"[bold]{Markup.Escape(text)}[/]";

    public static string SuccessMarkup(string text) => $"[green]{Markup.Escape(text)}[/]";

    public static string WarnMarkup(string text) => $"[yellow]{Markup.Escape(text)}[/]";

    public static string ErrorMarkup(string text) => $"[red]{Markup.Escape(text)}[/]";

    /// <summary>The REPL input glyph (accented).</summary>
    public static string PromptGlyph => $"[{Accent}]›[/]";
}
