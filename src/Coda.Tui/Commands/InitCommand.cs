using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Generates a CLAUDE.md for the current project by asking the model to analyze the codebase.</summary>
public sealed class InitCommand : ISlashCommand
{
    public string Name => "init";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Generate a CLAUDE.md memory file for this project";

    public CommandHelp Help => new(
        "/init",
        Description: "Asks the model to analyze the current project and writes a CLAUDE.md file summarizing its purpose, architecture, conventions, and build/test commands. Skipped (with a warning) if CLAUDE.md already exists.",
        Examples: ["/init"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(context.Session.WorkingDirectory, "CLAUDE.md");

        if (File.Exists(path))
        {
            context.Console.MarkupLine(Theme.WarnMarkup("CLAUDE.md already exists; not overwriting."));
            return CommandResult.Continue;
        }

        var options = new SessionOptions
        {
            ProviderId = context.Session.ActiveProviderId,
            Model = context.Session.Model,
            WorkingDirectory = context.Session.WorkingDirectory,
        };

        context.Console.MarkupLine(Theme.DimMarkup("Analyzing codebase to generate CLAUDE.md…"));

        using var session = new CodaSession(context.Credentials, options, history: []);
        var result = await session.RunAsync(
            "Analyze this codebase and write a concise CLAUDE.md that captures: the project purpose, " +
            "key architecture decisions, important conventions, build/test commands, and any gotchas. " +
            "Output ONLY the raw Markdown content for CLAUDE.md with no additional commentary or code fences.",
            sink: null,
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Could not generate CLAUDE.md: {result.Error ?? "unknown error"}"));
            return CommandResult.Continue;
        }

        if (string.IsNullOrWhiteSpace(result.FinalText))
        {
            context.Console.MarkupLine(Theme.ErrorMarkup("Model returned empty content; CLAUDE.md not written."));
            return CommandResult.Continue;
        }

        try
        {
            await File.WriteAllTextAsync(path, result.FinalText, cancellationToken).ConfigureAwait(false);
            context.Console.MarkupLine(Theme.SuccessMarkup($"CLAUDE.md written to {path}"));
        }
        catch (Exception ex)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Failed to write CLAUDE.md: {ex.Message}"));
        }

        return CommandResult.Continue;
    }
}
