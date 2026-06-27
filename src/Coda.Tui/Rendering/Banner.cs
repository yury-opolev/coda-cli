using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Rendering;

/// <summary>Renders the rebranded welcome banner (wordmark + version + cwd + hints).</summary>
public static class Banner
{
    public static void Render(IAnsiConsole console, SessionState session)
    {
        ArgumentNullException.ThrowIfNull(console);

        var heading = new Markup(
            $"{Theme.AccentMarkup($"Welcome to {Branding.ProductName}")} {Theme.DimMarkup($"v{Branding.Version}")}\n" +
            $"{Theme.DimMarkup(Branding.Tagline)}");

        var body = new Markup(
            $"{Theme.DimMarkup("cwd:")} {Markup.Escape(session.WorkingDirectory)}\n" +
            $"{Theme.DimMarkup("Type")} {Theme.AccentMarkup("/help")} {Theme.DimMarkup("for commands, or")} " +
            $"{Theme.AccentMarkup("/login")} {Theme.DimMarkup("to sign in.")} {Theme.DimMarkup("/exit to quit.")}");

        var rows = new Rows(heading, new Markup(string.Empty), body);
        var panel = new Panel(rows)
        {
            Header = new PanelHeader($" {Branding.ProductName} ", Justify.Left),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1, 2, 1),
        };
        panel.BorderStyle = new Style(foreground: Theme.AccentColor);

        console.WriteLine();
        WordmarkInto(console);
        console.Write(panel);
        console.WriteLine();
    }

    /// <summary>Big wordmark via Figlet, in the accent colour.</summary>
    private static void WordmarkInto(IAnsiConsole console)
    {
        var figlet = new FigletText(Branding.ProductName).LeftJustified().Color(Theme.AccentColor);
        console.Write(figlet);
    }
}
