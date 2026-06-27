using Coda.Tui.Plugins;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Manages plugin marketplaces: add, list, remove, browse, and install.</summary>
public sealed class MarketplaceCommand : ISlashCommand
{
    private readonly string? userPluginsDirOverride;

    public MarketplaceCommand() : this(null)
    {
    }

    /// <summary>Creates the command with an explicit plugins directory (for testing).</summary>
    public MarketplaceCommand(string? userPluginsDirOverride)
    {
        this.userPluginsDirOverride = userPluginsDirOverride;
    }

    public string Name => "marketplace";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Add, list, remove, browse, and install from plugin marketplaces";

    public CommandHelp Help => new(
        Usage: "/marketplace [list | add <source> | remove <name> | browse <name> | install <plugin> <marketplace>]",
        Description: "Manages plugin marketplaces (registries). Without a subcommand (or with 'list'), " +
            "shows registered marketplaces. Use 'add' to register a new marketplace from a URL or local path, " +
            "'remove' to unregister one, 'browse' to list plugins available in a marketplace, and " +
            "'install' to install a plugin from a named marketplace.",
        Options:
        [
            ("list", "List all registered marketplaces (default when no subcommand is given)."),
            ("add <source>", "Register a marketplace from a URL or local path."),
            ("remove <name>", "Unregister the named marketplace."),
            ("browse <name>", "List plugins available in the named marketplace."),
            ("install <plugin> <marketplace>", "Install the named plugin from the named marketplace."),
        ],
        Examples:
        [
            "/marketplace",
            "/marketplace list",
            "/marketplace add https://example.com/plugins/index.json",
            "/marketplace browse community",
            "/marketplace install my-plugin community",
            "/marketplace remove community",
        ]);

    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var subcommand = args.Count > 0 ? args[0].ToLowerInvariant() : "list";

        switch (subcommand)
        {
            case "list":
                return await this.ExecuteListAsync(context).ConfigureAwait(false);

            case "add":
                if (args.Count < 2)
                {
                    this.PrintUsage(context);
                    return CommandResult.Continue;
                }
                return await this.ExecuteAddAsync(context, args[1], cancellationToken).ConfigureAwait(false);

            case "remove":
                if (args.Count < 2)
                {
                    this.PrintUsage(context);
                    return CommandResult.Continue;
                }
                return this.ExecuteRemove(context, args[1]);

            case "browse":
                if (args.Count < 2)
                {
                    this.PrintUsage(context);
                    return CommandResult.Continue;
                }
                return await this.ExecuteBrowseAsync(context, args[1], cancellationToken).ConfigureAwait(false);

            case "install":
                if (args.Count < 3)
                {
                    this.PrintUsage(context);
                    return CommandResult.Continue;
                }
                return await this.ExecuteInstallAsync(context, args[1], args[2], cancellationToken).ConfigureAwait(false);

            default:
                context.Console.MarkupLine(Theme.WarnMarkup($"Unknown subcommand '{subcommand}'."));
                this.PrintUsage(context);
                return CommandResult.Continue;
        }
    }

    private Task<CommandResult> ExecuteListAsync(CommandContext context)
    {
        var manager = this.BuildManager();
        var marketplaces = manager.List();

        if (marketplaces.Count == 0)
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                "No marketplaces added. Use /marketplace add <source>."));
            return Task.FromResult(CommandResult.Continue);
        }

        context.Console.MarkupLine(Theme.BoldMarkup("Marketplaces"));
        var grid = new Grid().AddColumn().AddColumn().AddColumn();
        foreach (var (name, entry) in marketplaces)
        {
            var sourceSummary = GetSourceSummary(entry.Source);
            grid.AddRow(
                Theme.AccentMarkup(name),
                Theme.DimMarkup(sourceSummary),
                Theme.DimMarkup(entry.LastUpdated));
        }

        context.Console.Write(grid);
        context.Console.WriteLine();
        return Task.FromResult(CommandResult.Continue);
    }

    private async Task<CommandResult> ExecuteAddAsync(
        CommandContext context,
        string source,
        CancellationToken ct)
    {
        var manager = this.BuildManager();
        var (ok, message) = await manager.AddAsync(source, ct).ConfigureAwait(false);

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

    private CommandResult ExecuteRemove(CommandContext context, string name)
    {
        var manager = this.BuildManager();
        var (ok, message) = manager.Remove(name);

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

    private async Task<CommandResult> ExecuteBrowseAsync(
        CommandContext context,
        string name,
        CancellationToken ct)
    {
        var manager = this.BuildManager();
        var (ok, plugins, message) = await manager.GetPluginsAsync(name, ct).ConfigureAwait(false);

        if (!ok)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup(message));
            return CommandResult.Continue;
        }

        if (plugins.Count == 0)
        {
            context.Console.MarkupLine(Theme.DimMarkup("No plugins found in this marketplace."));
            return CommandResult.Continue;
        }

        var grid = new Grid().AddColumn().AddColumn().AddColumn();
        foreach (var plugin in plugins)
        {
            var version = plugin.Version ?? string.Empty;
            var description = plugin.Description ?? string.Empty;
            grid.AddRow(
                Theme.AccentMarkup(plugin.Name),
                Theme.DimMarkup(version),
                Theme.DimMarkup(description));
        }

        context.Console.Write(grid);
        context.Console.WriteLine();
        return CommandResult.Continue;
    }

    private async Task<CommandResult> ExecuteInstallAsync(
        CommandContext context,
        string pluginName,
        string marketplaceName,
        CancellationToken ct)
    {
        var manager = this.BuildManager();
        var (ok, message) = await manager.InstallPluginAsync(marketplaceName, pluginName, ct).ConfigureAwait(false);

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

    private void PrintUsage(CommandContext context)
    {
        context.Console.MarkupLine(Theme.WarnMarkup(
            "Usage: /marketplace [add <source> | list | remove <name> | browse <name> | install <plugin> <marketplace>]"));
    }

    private MarketplaceManager BuildManager()
    {
        return new MarketplaceManager(this.ResolveUserPluginsDir());
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

    private static string GetSourceSummary(MarketplaceSource source)
    {
        return source switch
        {
            GithubSource g => g.Repo,
            GitSource g => g.Url,
            LocalDirectorySource d => d.Path,
            LocalFileSource f => f.Path,
            _ => source.GetType().Name,
        };
    }
}
