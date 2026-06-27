using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Skills;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Lists all discovered skills from project and user skill directories.</summary>
public sealed class SkillsCommand : ISlashCommand
{
    public string Name => "skills";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "List available skills";

    public CommandHelp Help => new(
        Usage: "/skills",
        Description: "Lists all skills discovered from the project (.coda/skills/) and user (~/.coda/skills/) " +
            "skill directories. Each skill is a SKILL.md file that can be run via /skill <name>.",
        Examples:
        [
            "/skills",
        ]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var skills = SkillLoader.Load(context.Session.WorkingDirectory);

        if (skills.Count == 0)
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                "No skills found. Add SKILL.md files under .coda/skills/<name>/."));
            return Task.FromResult(CommandResult.Continue);
        }

        context.Console.MarkupLine(Theme.BoldMarkup("Skills"));
        var grid = new Grid().AddColumn().AddColumn();
        foreach (var skill in skills)
        {
            var description = string.IsNullOrWhiteSpace(skill.Description)
                ? string.Empty
                : skill.Description;
            grid.AddRow(Theme.AccentMarkup(skill.Name), Theme.DimMarkup(description));
        }

        context.Console.Write(grid);
        context.Console.WriteLine();
        return Task.FromResult(CommandResult.Continue);
    }
}
