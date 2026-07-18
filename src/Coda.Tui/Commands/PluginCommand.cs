using Coda.Tui.Plugins;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Manages plugins: install (from directory or git URL), remove, and list.</summary>
public sealed class PluginCommand : ISlashCommand
{
    private readonly string? userPluginsDirOverride;

    public PluginCommand() : this(null)
    {
    }

    /// <summary>Creates the command with an explicit plugins directory (for testing).</summary>
    public PluginCommand(string? userPluginsDirOverride)
    {
        this.userPluginsDirOverride = userPluginsDirOverride;
    }

    public string Name => "plugin";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Manage plugins: install, remove, list";

    public CommandHelp Help => new(
        Usage: "/plugin [list | install <source> | remove <name>]",
        Description: "Manages plugins. Without a subcommand (or with 'list'), lists installed plugins. " +
            "'install' accepts a local directory path or a git URL (http/https/git@). " +
            "'remove' uninstalls a plugin by name from the user plugins directory.",
        Options:
        [
            ("list", "List all installed plugins (default when no subcommand is given)."),
            ("install <source>", "Install a plugin from a local directory path or a git URL."),
            ("remove <name>", "Uninstall the named plugin from the user plugins directory."),
        ],
        Examples:
        [
            "/plugin",
            "/plugin list",
            "/plugin install ./my-plugin",
            "/plugin install https://github.com/example/coda-plugin.git",
            "/plugin remove my-plugin",
        ]);

    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        string subcommand;
        if (args.Count > 0)
        {
            subcommand = args[0].ToLowerInvariant();
        }
        else if (context.Prompts.IsInteractive)
        {
            // No subcommand given but a prompt surface can answer — ask which action to run.
            var chosen = await this.ChooseActionAsync(context, cancellationToken).ConfigureAwait(false);
            if (chosen is null)
            {
                return CommandResult.Continue;
            }

            subcommand = chosen;
        }
        else
        {
            subcommand = "list";
        }

        switch (subcommand)
        {
            case "list":
                return this.ExecuteList(context);

            case "install":
                if (args.Count < 2)
                {
                    context.Console.MarkupLine(Theme.WarnMarkup("Usage: /plugin install <path-or-git-url>"));
                    return CommandResult.Continue;
                }
                return await this.ExecuteInstallAsync(context, args[1], cancellationToken).ConfigureAwait(false);

            case "remove":
                if (args.Count < 2)
                {
                    context.Console.MarkupLine(Theme.WarnMarkup("Usage: /plugin remove <name>"));
                    return CommandResult.Continue;
                }
                return this.ExecuteRemove(context, args[1]);

            default:
                context.Console.MarkupLine(Theme.WarnMarkup(
                    $"Unknown subcommand '{subcommand}'. Usage: /plugin [list|install <source>|remove <name>]"));
                return CommandResult.Continue;
        }
    }

    /// <summary>
    /// Present the plugin action picker (title <c>Plugin action</c>, stable option ids
    /// <c>list/install/remove</c>) through the host-neutral prompt surface. Returns the chosen action
    /// id, or <c>null</c> when the user dismisses the prompt.
    /// </summary>
    internal async Task<string?> ChooseActionAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        var response = await context.Prompts.RequestAsync(
            UiPromptRequest.Select("Plugin action", new[]
            {
                new UiPromptOption("list", "List installed plugins"),
                new UiPromptOption("install", "Install a plugin"),
                new UiPromptOption("remove", "Remove a plugin"),
            }),
            cancellationToken).ConfigureAwait(false);

        return response.Cancelled || response.SelectedIds.Length == 0 ? null : response.SelectedIds[0];
    }

    private CommandResult ExecuteList(CommandContext context)
    {
        var plugins = PluginLoader.Load(context.Session.WorkingDirectory);

        if (plugins.Count == 0)
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                "No plugins installed. Use /plugin install <path-or-git-url> to add one."));
            return CommandResult.Continue;
        }

        context.Console.MarkupLine(Theme.BoldMarkup("Plugins"));
        var grid = new Grid().AddColumn().AddColumn().AddColumn();
        foreach (var plugin in plugins)
        {
            var versionText = $"v{plugin.Version}";
            var description = string.IsNullOrWhiteSpace(plugin.Description)
                ? string.Empty
                : plugin.Description;
            grid.AddRow(
                Theme.AccentMarkup(plugin.Name),
                Theme.DimMarkup(versionText),
                Theme.DimMarkup(description));
        }

        context.Console.Write(grid);
        context.Console.WriteLine();
        return CommandResult.Continue;
    }

    private async Task<CommandResult> ExecuteInstallAsync(
        CommandContext context,
        string source,
        CancellationToken ct)
    {
        var userPluginsDir = this.ResolveUserPluginsDir();

        (bool ok, string message) result;
        if (IsGitUrl(source))
        {
            result = await PluginInstaller.InstallFromGitAsync(userPluginsDir, source, ct)
                .ConfigureAwait(false);
        }
        else
        {
            result = await PluginInstaller.InstallFromDirectoryAsync(userPluginsDir, source, ct)
                .ConfigureAwait(false);
        }

        if (result.ok)
        {
            context.Console.MarkupLine(Theme.SuccessMarkup(result.message));
        }
        else
        {
            context.Console.MarkupLine(Theme.ErrorMarkup(result.message));
        }

        return CommandResult.Continue;
    }

    private CommandResult ExecuteRemove(CommandContext context, string name)
    {
        var userPluginsDir = this.ResolveUserPluginsDir();
        var (ok, message) = PluginInstaller.Remove(userPluginsDir, name);

        if (ok)
        {
            context.Console.MarkupLine(Theme.SuccessMarkup(message));
        }
        else
        {
            context.Console.MarkupLine(Theme.ErrorMarkup(message));
        }

        return CommandResult.Continue;
    }

    private string ResolveUserPluginsDir()
    {
        if (this.userPluginsDirOverride is not null)
        {
            return this.userPluginsDirOverride;
        }

        var userCodaDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".coda");
        return Path.Combine(userCodaDir, "plugins");
    }

    private static bool IsGitUrl(string source)
    {
        return source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith(".git", StringComparison.OrdinalIgnoreCase);
    }
}
