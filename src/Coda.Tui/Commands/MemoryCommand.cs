using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Shows the path to the project CLAUDE.md and its contents (if it exists).</summary>
public sealed class MemoryCommand : ISlashCommand
{
    public string Name => "memory";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show the project CLAUDE.md memory file";

    public CommandHelp Help => new(
        "/memory",
        Description: "Prints the path to the project CLAUDE.md in the current working directory, then displays its contents. If CLAUDE.md does not exist, suggests running /init to generate one.",
        Examples: ["/memory"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(context.Session.WorkingDirectory, "CLAUDE.md");
        context.Console.MarkupLine(Theme.DimMarkup($"CLAUDE.md path: {path}"));

        if (!File.Exists(path))
        {
            context.Console.MarkupLine(Theme.WarnMarkup("CLAUDE.md not found. Run /init to generate one for this project."));
            return CommandResult.Continue;
        }

        var contents = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        context.Console.WriteLine(contents);
        return CommandResult.Continue;
    }
}
