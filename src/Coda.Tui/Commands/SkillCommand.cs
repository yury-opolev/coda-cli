using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Skills;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Runs a named skill by injecting its body as an agent prompt, with argument substitution.</summary>
public sealed class SkillCommand : ISlashCommand
{
    public string Name => "skill";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Run a skill by name (or list skills if no name given)";

    public CommandHelp Help => new(
        Usage: "/skill [<name> [args...]]",
        Description: "Runs the named skill by injecting its SKILL.md body as an agent prompt. " +
            "Arguments after the name are substituted into $1, $2, $name, and $ARGUMENTS placeholders. " +
            "Skills are discovered from .coda/skills/<name>/ (project) and ~/.coda/skills/<name>/ (user). " +
            "With no argument, lists available skills (same as /skills).",
        Options:
        [
            ("<name>", "Name of the skill to run. Case-insensitive. Omit to list all available skills."),
            ("[args...]", "Arguments substituted into $1, $2, $name, and $ARGUMENTS placeholders in the body."),
        ],
        Examples:
        [
            "/skill",
            "/skill code-review",
            "/skill translate French \"Hello world\"",
        ]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var skills = SkillLoader.Load(context.Session.WorkingDirectory);

        // No arguments → behave like /skills (list), showing only user-invocable skills.
        if (args.Count == 0)
        {
            return ListSkillsAsync(context, skills.Where(s => s.UserInvocable).ToList());
        }

        var requestedName = args[0];
        IReadOnlyList<string> invokeArgs = args.Count > 1
            ? [.. args.Skip(1)]
            : [];

        var skill = skills.FirstOrDefault(s =>
            string.Equals(s.Name, requestedName, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
        {
            var userVisible = skills.Where(s => s.UserInvocable).ToList();
            var available = userVisible.Count > 0
                ? string.Join(", ", userVisible.Select(s => s.Name))
                : "(none)";
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Skill '{requestedName}' not found. Available: {available}"));
            return Task.FromResult(CommandResult.Continue);
        }

        if (!skill.UserInvocable)
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Skill '{skill.Name}' is model-invocable only and cannot be run with /skill."));
            return Task.FromResult(CommandResult.Continue);
        }

        var body = (invokeArgs.Count > 0 || skill.Arguments.Count > 0)
            ? SkillArgumentBinder.Bind(skill.Body, skill.Arguments, invokeArgs)
            : skill.Body;
        return Task.FromResult(CommandResult.RunPrompt(body));
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
            var hintSuffix = skill.ArgumentHint is not null
                ? "  " + Theme.DimMarkup(skill.ArgumentHint)
                : string.Empty;
            grid.AddRow(Theme.AccentMarkup(skill.Name), Theme.DimMarkup(description) + hintSuffix);
        }

        context.Console.Write(grid);
        context.Console.WriteLine();
        return Task.FromResult(CommandResult.Continue);
    }
}
