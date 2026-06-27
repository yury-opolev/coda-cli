using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Rendering;

/// <summary>
/// Renders a command's <see cref="CommandHelp"/> to the console uniformly: a header
/// with name + aliases, the usage line, the description, an options grid, and an
/// examples block. The single TUI presenter for help.
/// </summary>
public static class CommandHelpRenderer
{
    /// <summary>
    /// Writes the full help for <paramref name="command"/> to <paramref name="console"/>.
    /// </summary>
    public static void Render(IAnsiConsole console, ISlashCommand command)
    {
        var help = command.Help;

        var header = $"/{command.Name}";
        if (command.Aliases.Count > 0)
        {
            var aliases = string.Join(", ", command.Aliases.Select(a => $"/{a}"));
            header += $"  (alias: {aliases})";
        }

        console.MarkupLine(Theme.BoldMarkup(header));
        console.MarkupLine($"{Theme.DimMarkup("Usage:")} {Theme.AccentMarkup(help.Usage)}");

        if (!string.IsNullOrWhiteSpace(help.Description))
        {
            console.WriteLine();
            console.MarkupLine(Theme.DimMarkup(help.Description));
        }

        if (help.Options is { Count: > 0 })
        {
            console.WriteLine();
            var grid = new Grid().AddColumn().AddColumn();
            foreach (var (arg, meaning) in help.Options)
            {
                grid.AddRow(Theme.AccentMarkup(arg), Theme.DimMarkup(meaning));
            }

            console.Write(grid);
        }

        if (help.Examples is { Count: > 0 })
        {
            console.WriteLine();
            console.MarkupLine(Theme.DimMarkup("Examples:"));
            foreach (var example in help.Examples)
            {
                console.MarkupLine($"  {Theme.AccentMarkup(example)}");
            }
        }

        console.WriteLine();
    }
}
