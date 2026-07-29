using Coda.Tui.Commands;
using Coda.Tui.Skills;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Repl;

/// <summary>
/// The single source of truth for the set of slash commands. Both the interactive
/// TUI registry and the headless <c>coda help</c> runner build their command set
/// from here, so the two never diverge.
/// </summary>
public static class SlashCommandCatalog
{
    /// <summary>Creates one instance of every slash command, in display order.</summary>
    public static IReadOnlyList<ISlashCommand> CreateAll() =>
    [
        new HelpCommand(),
        new SetupCommand(),
        new LoginCommand(),
        new LogoutCommand(),
        new StatusCommand(),
        new TasksCommand(),
        new ScheduleCommand(),
        new ProviderCommand(),
        new ModelCommand(),
        new EffortCommand(),
        new LogCommand(),
        new GoalCommand(),
        new OutputStyleCommand(),
        new ThemeCommand(),
        new PermissionsCommand(),
        new YoloCommand(),
        new HeadersCommand(),
        new ClearCommand(),
        new ResumeCommand(),
        new ForkCommand(),
        new RewindCommand(),
        new SkillsCommand(),
        new SkillCommand(),
        new PluginsCommand(),
        new PluginCommand(),
        new MarketplaceCommand(),
        new InitCommand(),
        new MemoryCommand(),
        new McpCommand(),
        new HooksCommand(),
        new CompactCommand(),
        new ContextCommand(),
        new CostCommand(),
        new ImageCommand(),
        new ExportCommand(),
        new ImportCommand(),
        new DiffCommand(),
        new DoctorCommand(),
        new VersionCommand(),
        new ExitCommand(),
    ];

    /// <summary>
    /// Creates the full command set: all built-ins (from <see cref="CreateAll"/>) plus
    /// one <see cref="Coda.Tui.Commands.SkillSlashCommand"/> per user-invocable skill in
    /// <paramref name="skills"/> that does not collide with a built-in name or alias.
    /// This is the composition root entry point for skill-derived slash commands.
    /// </summary>
    /// <param name="skills">Discovered, precedence-resolved skills for the current session.</param>
    /// <param name="logger">Optional logger for name-collision warnings.</param>
    public static IReadOnlyList<ISlashCommand> CreateWithSkills(
        IReadOnlyList<SkillDefinition> skills,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var builtIns = CreateAll();
        var skillCommands = SkillCommandRegistrar.BuildSkillCommands(skills, builtIns, logger);
        return [.. builtIns, .. skillCommands];
    }
}