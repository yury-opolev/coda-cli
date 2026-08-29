using Coda.Common;
using Coda.Tui.Plugins;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Skills;
using Microsoft.Extensions.Logging;
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
        Usage: "/skills [list | info <name> | enable <name> | disable <name> | reload | validate <path> | new <name>]",
        Description: "Lists, inspects, validates, scaffolds, and reloads skills. Without arguments, lists all discovered skills " +
            "from the project (.coda/skills/) and user (~/.coda/skills/) skill directories. " +
            "Each skill is a SKILL.md file that can be run via /skill <name> or, for user-invocable skills, directly as /<name>.",
        Options:
        [
            ("(no args) / list", "List all discovered skills with name, description, and argument hint."),
            ("info <name>", "Show detailed information about a skill: origin, source path, argument hint, and flags."),
            ("enable <name>", "Allow the model to invoke the skill (clears disable-model-invocation in its frontmatter)."),
            ("disable <name>", "Prevent the model from invoking the skill (sets disable-model-invocation in its frontmatter)."),
            ("reload", "Re-scan skill directories and re-register skill-derived slash commands."),
            ("validate <path>", "Parse and validate the SKILL.md at <path> (or <path>/SKILL.md if a directory)."),
            ("new <name>", "Scaffold a new skill at <cwd>/.coda/skills/<name>/SKILL.md."),
        ],
        Examples:
        [
            "/skills",
            "/skills list",
            "/skills info my-skill",
            "/skills enable my-skill",
            "/skills disable my-skill",
            "/skills reload",
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
            "info" => InfoAsync(context, tail),
            "enable" => SetEnabledAsync(context, tail, enabled: true),
            "disable" => SetEnabledAsync(context, tail, enabled: false),
            "reload" => ReloadAsync(context),
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

    // ── info ─────────────────────────────────────────────────────────────

    private static Task<CommandResult> InfoAsync(CommandContext context, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            context.Console.MarkupLine(Theme.WarnMarkup("Usage: /skills info <name>"));
            return Task.FromResult(CommandResult.Continue);
        }

        var name = args[0];
        var skills = SkillLoader.Load(context.Session.WorkingDirectory, pluginStateStore: context.PluginState);
        var skill = skills.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Skill '{name}' not found. Use /skills to see discovered skills."));
            return Task.FromResult(CommandResult.Continue);
        }

        context.Console.MarkupLine(Theme.BoldMarkup(skill.Name));
        var grid = new Grid().AddColumn().AddColumn();

        if (!string.IsNullOrWhiteSpace(skill.Description))
        {
            grid.AddRow(Theme.DimMarkup("description"), skill.Description);
        }

        grid.AddRow(Theme.DimMarkup("origin"), skill.Origin.ToString().ToLowerInvariant());

        if (skill.SourcePath is { Length: > 0 } sourcePath)
        {
            grid.AddRow(Theme.DimMarkup("source"), sourcePath);
        }

        if (skill.ArgumentHint is { Length: > 0 } hint)
        {
            grid.AddRow(Theme.DimMarkup("argument hint"), hint);
        }

        grid.AddRow(
            Theme.DimMarkup("model invocation"),
            skill.DisableModelInvocation ? Theme.WarnMarkup("disabled") : Theme.SuccessMarkup("enabled"));
        grid.AddRow(
            Theme.DimMarkup("user invocable"),
            skill.UserInvocable ? Theme.SuccessMarkup("yes") : Theme.WarnMarkup("no"));

        if (skill.ContextMode == SkillContextMode.Fork)
        {
            var agent = skill.AgentType is { Length: > 0 } at ? at : "general-purpose";
            grid.AddRow(Theme.DimMarkup("context mode"), $"fork ({agent})");
        }

        context.Console.Write(grid);
        context.Console.WriteLine();
        return Task.FromResult(CommandResult.Continue);
    }

    // ── enable / disable ─────────────────────────────────────────────────

    /// <summary>
    /// Toggles a skill's <c>disable-model-invocation</c> frontmatter flag in its own SKILL.md so the
    /// change persists across sessions. Enable clears the flag; disable sets it. Only skills loaded
    /// from a writable on-disk source (with frontmatter) can be toggled; read-only foreign/Claude
    /// skills are refused.
    /// </summary>
    private static Task<CommandResult> SetEnabledAsync(
        CommandContext context, IReadOnlyList<string> args, bool enabled)
    {
        var verb = enabled ? "enable" : "disable";
        if (args.Count == 0)
        {
            context.Console.MarkupLine(Theme.WarnMarkup($"Usage: /skills {verb} <name>"));
            return Task.FromResult(CommandResult.Continue);
        }

        var name = args[0];
        var skills = SkillLoader.Load(context.Session.WorkingDirectory, pluginStateStore: context.PluginState);
        var skill = skills.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Skill '{name}' not found. Use /skills to see discovered skills."));
            return Task.FromResult(CommandResult.Continue);
        }

        if (skill.Origin is SkillOrigin.Foreign or SkillOrigin.Claude or SkillOrigin.Plugin)
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Skill '{skill.Name}' is read-only ({skill.Origin.ToString().ToLowerInvariant()} origin) and cannot be toggled."));
            return Task.FromResult(CommandResult.Continue);
        }

        if (skill.SourcePath is not { Length: > 0 } sourcePath || !File.Exists(sourcePath))
        {
            context.Console.MarkupLine(Theme.ErrorMarkup(
                $"Skill '{skill.Name}' has no writable source file to update."));
            return Task.FromResult(CommandResult.Continue);
        }

        try
        {
            var content = File.ReadAllText(sourcePath);
            var updated = SetDisableModelInvocationFlag(content, disable: !enabled);
            // Atomic write: write to a sibling temp file then rename so a crash mid-write
            // never leaves the skill file truncated.
            var dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
            AtomicFile.WriteAllText(sourcePath, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Failed to update skill: {ex.Message}"));
            return Task.FromResult(CommandResult.Continue);
        }

        context.Console.MarkupLine(Theme.SuccessMarkup(
            $"Skill '{skill.Name}' {(enabled ? "enabled" : "disabled")} for model invocation."));
        return Task.FromResult(CommandResult.Continue);
    }

    /// <summary>
    /// Rewrites the <c>disable-model-invocation</c> line inside a SKILL.md's YAML frontmatter block.
    /// When <paramref name="disable"/> is true the flag is set to <c>true</c> (inserting it if absent);
    /// otherwise the flag line is removed so the default (enabled) applies. Files without a leading
    /// frontmatter block are wrapped in one so the flag has somewhere to live.
    /// </summary>
    internal static string SetDisableModelInvocationFlag(string content, bool disable)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = new List<string>(normalized.Split('\n'));

        const string flagKey = "disable-model-invocation";

        // Locate the frontmatter block: opening '---' on the first non-empty line, closing '---'.
        var firstNonEmpty = lines.FindIndex(l => l.Trim().Length > 0);
        var hasFrontmatter = firstNonEmpty >= 0 && lines[firstNonEmpty].Trim() == "---";

        if (!hasFrontmatter)
        {
            // No frontmatter — synthesize a minimal block when disabling; nothing to do when enabling.
            if (!disable)
            {
                return content;
            }

            var header = new List<string> { "---", $"{flagKey}: true", "---" };
            header.AddRange(lines);
            return string.Join(newline, header);
        }

        var closeIndex = lines.FindIndex(firstNonEmpty + 1, l => l.Trim() == "---");
        if (closeIndex < 0)
        {
            closeIndex = lines.Count; // Malformed; treat the rest as frontmatter.
        }

        // Remove any existing flag line within the block.
        // Only match lines at the top indentation level (no leading whitespace) so YAML
        // block-scalar continuation lines that happen to start with the flag key are never
        // mistakenly deleted.
        for (var i = closeIndex - 1; i > firstNonEmpty; i--)
        {
            var line = lines[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) &&
                line.StartsWith(flagKey, StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(i);
                closeIndex--;
            }
        }

        if (disable)
        {
            lines.Insert(closeIndex, $"{flagKey}: true");
        }

        return string.Join(newline, lines);
    }

    private static Task<CommandResult> ReloadAsync(CommandContext context)
    {
        var skills = SkillLoader.Load(context.Session.WorkingDirectory, pluginStateStore: context.PluginState);
        var builtIns = SlashCommandCatalog.CreateAll();

        // Thread a real logger so collision and name-validation warnings appear as
        // user-visible messages rather than being silently swallowed.
        var logger = new AnsiConsoleLogger(context.Console);
        var skillCommands = SkillCommandRegistrar.BuildSkillCommands(skills, builtIns, logger);
        context.Commands.ReplaceAll([.. builtIns, .. skillCommands]);

        // Report the actual registered count (not the raw user-invocable count), plus any
        // skill names that were skipped so the author can see why their /name did not appear.
        var registeredCount = skillCommands.Count;
        var skippedNames = skills
            .Where(s => s.UserInvocable && !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => s.Name)
            .Except(skillCommands.Select(c => c.Name), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var message = skippedNames.Count > 0
            ? $"Skills reloaded. {registeredCount} skill command(s) registered. " +
              $"Skipped: {string.Join(", ", skippedNames.Select(n => "/" + n))}."
            : $"Skills reloaded. {registeredCount} skill command(s) registered.";

        context.Console.MarkupLine(Theme.SuccessMarkup(message));
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
                "characters that are invalid in a directory name, a trailing dot, " +
                "surrounding whitespace, or a reserved device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9)."));
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
    /// name. Delegates to <see cref="SafeNameValidator.IsValidName"/>, the same validator plugin
    /// installation uses, so skill and plugin names cannot drift apart.
    /// </summary>
    private static bool IsValidSkillName(string name) => SafeNameValidator.IsValidName(name);

    private static string BuildTemplate(string name) =>
        $"---{Environment.NewLine}name: {name}{Environment.NewLine}description: Describe your skill here.{Environment.NewLine}---{Environment.NewLine}# {name}{Environment.NewLine}{Environment.NewLine}Add your skill prompt body here.{Environment.NewLine}";
}

/// <summary>
/// Minimal <see cref="ILogger"/> adapter that routes <see cref="LogLevel.Warning"/> and above
/// to the Spectre.Console <see cref="IAnsiConsole"/> as styled warning markup. Used by
/// <c>/skills reload</c> so collision and name-validation warnings are visible to the user.
/// </summary>
file sealed class AnsiConsoleLogger(IAnsiConsole console) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
        {
            console.MarkupLine(Theme.WarnMarkup(formatter(state, exception)));
        }
    }
}
