using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Rendering;

/// <summary>Renders the rebranded welcome banner (wordmark + version + cwd + hints).</summary>
public static class Banner
{
    public static void Render(IAnsiConsole console, SessionState session, string? connectedProvider = null, string? model = null)
    {
        ArgumentNullException.ThrowIfNull(console);

        var heading = new Markup(
            $"{Theme.AccentMarkup($"Welcome to {Branding.ProductName}")} {Theme.DimMarkup($"v{Branding.Version}")}\n" +
            $"{Theme.DimMarkup(Branding.Tagline)}");

        var providerLine = connectedProvider is null
            ? Theme.DimMarkup("not signed in — run ") + Theme.AccentMarkup("/login")
            : $"{Theme.DimMarkup("provider:")} {Markup.Escape(connectedProvider)}   {Theme.DimMarkup("model:")} {Markup.Escape(model ?? "—")}";

        var body = new Markup(
            $"{Theme.DimMarkup("cwd:")} {Markup.Escape(session.WorkingDirectory)}\n" +
            $"{providerLine}\n" +
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

    /// <summary>Big embedded Unicode wordmark, in the accent colour.</summary>
    private static void WordmarkInto(IAnsiConsole console)
    {
        var wordmark = string.Join(Environment.NewLine, Branding.BannerLines);
        console.Write(new Text(wordmark, new Style(foreground: Theme.AccentColor)));
        console.WriteLine();
    }
}
