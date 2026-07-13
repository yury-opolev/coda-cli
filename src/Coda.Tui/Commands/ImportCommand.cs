using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Imports a *.coda-session.json bundle into this workspace and reports the id to resume.</summary>
public sealed class ImportCommand : ISlashCommand
{
    public string Name => "import";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Import a session bundle file into this workspace";

    public CommandHelp Help => new(
        "/import <file>",
        Description: "Imports a *.coda-session.json bundle (from /export --json or `coda export`) into this working directory's sessions, then reports the id to resume with /resume.",
        Options: [("<file>", "path to a coda-session.json bundle (relative paths resolve from the working directory)")],
        Examples: ["/import session.coda-session.json"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            context.Console.MarkupLine(Theme.ErrorMarkup("Usage: /import <file>"));
            return CommandResult.Continue;
        }

        var path = Path.IsPathRooted(args[0]) ? args[0] : Path.Combine(context.Session.WorkingDirectory, args[0]);
        if (!File.Exists(path))
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"File not found: {path}"));
            return CommandResult.Continue;
        }

        try
        {
            var service = new SessionBundleService(context.Session.WorkingDirectory, Branding.Version);
            var id = await service.ImportAsync(path, cancellationToken).ConfigureAwait(false);
            context.Console.MarkupLine(Theme.DimMarkup($"Imported as {id}. Use /resume {id} to continue."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Import failed: {ex.Message}"));
        }

        return CommandResult.Continue;
    }
}
