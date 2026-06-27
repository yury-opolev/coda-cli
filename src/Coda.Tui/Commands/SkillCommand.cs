using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Skills;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Runs a named skill by injecting its body as an agent prompt.</summary>
public sealed class SkillCommand : ISlashCommand
{
    public string Name => "skill";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Run a skill by name (or list skills if no name given)";

    public CommandHelp Help => new(
        Usage: "/skill [<name>]",
        Description: "Runs the named skill by injecting its SKILL.md body as an agent prompt. " +
            "Skills are discovered from .coda/skills/<name>/ (project) and ~/.coda/skills/<name>/ (user). " +
            "With no argument, lists available skills (same as /skills).",
        Options:
        [
            ("<name>", "Name of the skill to run. Case-insensitive. Omit to list all available skills."),
        ],
        Examples:
        [
            "/skill",
            "/skill code-review",
            "/skill brainstorming",
        ]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var skills = SkillLoader.Load(context.Session.WorkingDirectory);

        // No arguments → behave like /skills (list).
        if (args.Count == 0)
        {
            return ListSkillsAsync(context, skills);
        }

        var requestedName = args[0];
        var skill = skills.FirstOrDefault(s =>
            string.Equals(s.Name, requestedName, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
        {
            var available = skills.Count > 0
                ? string.Join(", ", skills.Select(s => s.Name))
                : "(none)";
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Skill '{requestedName}' not found. Available: {available}"));
            return Task.FromResult(CommandResult.Continue);
        }

        return Task.FromResult(CommandResult.RunPrompt(skill.Body));
    }

    private static Task<CommandResult> ListSkillsAsync(CommandContext context, IReadOnlyList<SkillDefinition> skills)
    {
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
