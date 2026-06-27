using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Lists the available slash commands, or — with an argument — shows detailed help for
/// one command (the same content as <c>/&lt;command&gt; --help</c>).
/// </summary>
public sealed class HelpCommand : ISlashCommand
{
    public string Name => "help";

    public IReadOnlyList<string> Aliases => ["?"];

    public string Summary => "Show available commands, or detailed help for one";

    public CommandHelp Help => new(
        "/help [<command>]",
        Description: "With no argument, lists every command. With a command name (or alias), shows that command's usage, arguments, and examples — the same as /<command> --help.",
        Options: [("<command>", "show detailed help for this command")],
        Examples: ["/help", "/help log", "/log --help"]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (args.Count > 0)
        {
            this.ShowCommandHelp(context, args[0]);
            return Task.FromResult(CommandResult.Continue);
        }

        this.ShowList(context);
        return Task.FromResult(CommandResult.Continue);
    }

    private void ShowList(CommandContext context)
    {
        context.Console.MarkupLine(Theme.BoldMarkup("Commands"));
        var grid = new Grid().AddColumn().AddColumn();
        foreach (var command in context.Commands.ListSorted())
        {
            grid.AddRow(Theme.AccentMarkup($"/{command.Name}"), Theme.DimMarkup(command.Summary));
        }

        context.Console.Write(grid);
        context.Console.WriteLine();
        context.Console.MarkupLine(Theme.DimMarkup("Type /help <command> or /<command> --help for details."));
    }

    private void ShowCommandHelp(CommandContext context, string name)
    {
        var token = name.TrimStart('/');
        var command = context.Commands.Resolve(token);
        if (command is not null)
        {
            CommandHelpRenderer.Render(context.Console, command);
            return;
        }

        var suggestion = context.Commands.ListSorted()
            .FirstOrDefault(c => c.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                || c.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
        var hint = suggestion is not null ? $" Did you mean /{suggestion.Name}?" : string.Empty;
        context.Console.MarkupLine(Theme.WarnMarkup($"Unknown command '/{token}'.") + Theme.DimMarkup(hint));
    }
}
