using Coda.Tui.Plugins;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Skills;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Lists, validates, and scaffolds skills from project and user skill directories.</summary>
public sealed class SkillsCommand : ISlashCommand
{
    private const string SkillFileName = "SKILL.md";

    public string Name => "skills";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "List, validate, and scaffold skills";

    public CommandHelp Help => new(
        Usage: "/skills [list | validate <path> | new <name>]",
        Description: "Lists, validates, and scaffolds skills. Without arguments, lists all discovered skills " +
            "from the project (.coda/skills/) and user (~/.coda/skills/) skill directories. " +
            "Each skill is a SKILL.md file that can be run via /skill <name>.",
        Options:
        [
            ("(no args) / list", "List all discovered skills with name, description, and argument hint."),
            ("validate <path>", "Parse and validate the SKILL.md at <path> (or <path>/SKILL.md if a directory)."),
            ("new <name>", "Scaffold a new skill at <cwd>/.coda/skills/<name>/SKILL.md."),
        ],
        Examples:
        [
            "/skills",
            "/skills list",
            "/skills validate .coda/skills/my-skill",
            "/skills new my-feature",
        ]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var subcommand = args.Count > 0 ? args[0].ToLowerInvariant() : "list";
        var tail = args.Count > 1 ? (IReadOnlyList<string>)[.. args.Skip(1)] : [];

        return subcommand switch
        {
            "list" or "" => this.ListAsync(context),
            "validate" => ValidateAsync(context, tail),
            "new" => NewAsync(context, tail, context.Session.WorkingDirectory),
            _ => this.ListAsync(context),
        };
    }

    // ── list ─────────────────────────────────────────────────────────────

    private Task<CommandResult> ListAsync(CommandContext context)
    {
        var allSkills = SkillLoader.Load(context.Session.WorkingDirectory);
        var skills = allSkills.Where(s => s.UserInvocable).ToList();

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

    // ── validate ─────────────────────────────────────────────────────────

    private static Task<CommandResult> ValidateAsync(CommandContext context, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            context.Console.MarkupLine(Theme.WarnMarkup("Usage: /skills validate <path>"));
            return Task.FromResult(CommandResult.Continue);
        }

        var inputPath = args[0];

        // If a directory is given, look for SKILL.md inside it.
        var filePath = Directory.Exists(inputPath)
            ? Path.Combine(inputPath, SkillFileName)
            : inputPath;

        if (!File.Exists(filePath))
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"File not found: {filePath}"));
            return Task.FromResult(CommandResult.Continue);
        }

        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Cannot read file: {ex.Message}"));
            return Task.FromResult(CommandResult.Continue);
        }

        var fm = SkillFrontmatterParser.Parse(content);

        context.Console.MarkupLine(Theme.BoldMarkup($"Validating {filePath}"));

        var grid = new Grid().AddColumn().AddColumn();

        // Name
        var nameValue = string.IsNullOrWhiteSpace(fm.Name) ? "(missing)" : fm.Name;
        var nameMarkup = string.IsNullOrWhiteSpace(fm.Name)
            ? Theme.WarnMarkup(nameValue)
            : Theme.AccentMarkup(nameValue);
        grid.AddRow(Theme.DimMarkup("name"), nameMarkup);

        // Description
        var descValue = string.IsNullOrWhiteSpace(fm.Description) ? "(missing)" : fm.Description;
        var descMarkup = string.IsNullOrWhiteSpace(fm.Description)
            ? Theme.WarnMarkup(descValue)
            : Theme.DimMarkup(descValue);
        grid.AddRow(Theme.DimMarkup("description"), descMarkup);

        // Frontmatter presence
        grid.AddRow(
            Theme.DimMarkup("frontmatter"),
            fm.HasFrontmatter
                ? Theme.SuccessMarkup("present")
                : Theme.WarnMarkup("absent"));

        // Unknown keys (only shown when present)
        if (fm.UnknownFields.Count > 0)
        {
            var unknownKeys = string.Join(", ", fm.UnknownFields.Keys);
            grid.AddRow(Theme.DimMarkup("unknown keys"), Theme.DimMarkup(unknownKeys));
        }

        // Problems
        var problems = new List<string>();
        if (!fm.HasFrontmatter)
        {
            problems.Add("no frontmatter");
        }

        if (string.IsNullOrWhiteSpace(fm.Name))
        {
            problems.Add("missing name");
        }

        if (string.IsNullOrWhiteSpace(fm.Description))
        {
            problems.Add("missing description");
        }

        var problemsMarkup = problems.Count > 0
            ? Theme.WarnMarkup(string.Join("; ", problems))
            : Theme.SuccessMarkup("none");
        grid.AddRow(Theme.DimMarkup("problems"), problemsMarkup);

        context.Console.Write(grid);
        context.Console.WriteLine();

        return Task.FromResult(CommandResult.Continue);
    }

    // ── new ───────────────────────────────────────────────────────────────

    private static Task<CommandResult> NewAsync(
        CommandContext context,
        IReadOnlyList<string> args,
        string workingDirectory)
    {
        if (args.Count == 0)
        {
            context.Console.MarkupLine(Theme.WarnMarkup("Usage: /skills new <name>"));
            return Task.FromResult(CommandResult.Continue);
        }

        var name = args[0];

        if (!IsValidSkillName(name))
        {
            context.Console.MarkupLine(Theme.ErrorMarkup(
                $"Invalid skill name '{name}': must not contain path separators, '..', " +
                "or characters that are invalid in a directory name."));
            return Task.FromResult(CommandResult.Continue);
        }

        var skillDir = Path.Combine(workingDirectory, ".coda", "skills", name);

        if (Directory.Exists(skillDir))
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Skill '{name}' already exists at {skillDir}"));
            return Task.FromResult(CommandResult.Continue);
        }

        try
        {
            Directory.CreateDirectory(skillDir);
            var skillFile = Path.Combine(skillDir, SkillFileName);
            File.WriteAllText(skillFile, BuildTemplate(name));
            context.Console.MarkupLine(Theme.SuccessMarkup($"Created {skillFile}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Failed to create skill: {ex.Message}"));
        }

        return Task.FromResult(CommandResult.Continue);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="name"/> is a safe single-segment skill
    /// name: no <c>..</c> substring, no path separators, and no characters invalid in a directory
    /// name. Mirrors the defensive validation in <see cref="PluginInstaller.IsValidPluginName"/>.
    /// </summary>
    private static bool IsValidSkillName(string name)
    {
        if (name.Contains(".."))
        {
            return false; // path traversal
        }

        return PluginInstaller.IsValidPluginName(name);
    }

    private static string BuildTemplate(string name) =>
        $"---{Environment.NewLine}name: {name}{Environment.NewLine}description: Describe your skill here.{Environment.NewLine}---{Environment.NewLine}# {name}{Environment.NewLine}{Environment.NewLine}Add your skill prompt body here.{Environment.NewLine}";
}
