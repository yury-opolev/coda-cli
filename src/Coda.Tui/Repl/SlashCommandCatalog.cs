using Coda.Tui.Commands;

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
        new ProviderCommand(),
        new ModelCommand(),
        new EffortCommand(),
        new LogCommand(),
        new GoalCommand(),
        new OutputStyleCommand(),
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
}
