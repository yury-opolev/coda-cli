using Coda.Tui.Plugins;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Manages plugin marketplaces: add, list, remove, browse, install, refresh, and search.</summary>
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

    public string Summary => "Add, list, remove, browse, search, refresh, and install from plugin marketplaces";

    public CommandHelp Help => new(
        Usage: "/marketplace [list | add <source> | remove <name> [--force] | browse <name> | install <plugin> <marketplace> | search <query> | refresh [<name>]]",
        Description: "Manages plugin marketplaces (registries). Without a subcommand (or with 'list'), " +
            "shows registered marketplaces. Use 'add' to register a new marketplace from a URL or local path, " +
            "'remove' to unregister one (--force bypasses the dependent-plugin check), " +
            "'browse' to list plugins available in a marketplace, " +
            "'install' to install a plugin from a named marketplace, " +
            "'search' to search across all marketplaces, and " +
            "'refresh' to re-fetch and update a marketplace (or all if no name given).",
        Options:
        [
            ("list", "List all registered marketplaces (default when no subcommand is given)."),
            ("add <source>", "Register a marketplace from a URL or local path."),
            ("remove <name> [--force]", "Unregister the named marketplace. --force overrides dependent-plugin check."),
            ("browse <name>", "List plugins available in the named marketplace."),
            ("install <plugin> <marketplace>", "Install the named plugin from the named marketplace."),
            ("search <query>", "Search across all marketplaces by name, description, category, or tags."),
            ("refresh [<name>]", "Re-fetch a marketplace manifest and report changes. Omit name to refresh all."),
        ],
        Examples:
        [
            "/marketplace",
            "/marketplace list",
            "/marketplace add https://example.com/plugins/index.json",
            "/marketplace browse community",
            "/marketplace install my-plugin community",
            "/marketplace remove community",
            "/marketplace remove community --force",
            "/marketplace search linter",
            "/marketplace refresh",
            "/marketplace refresh community",
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
                return await this.ExecuteListAsync(context).ConfigureAwait(false);

            case "add":
                if (args.Count < 2)
                {
                    this.PrintUsage(context);
                    return CommandResult.Continue;
                }
                return await this.ExecuteAddAsync(context, args[1], cancellationToken).ConfigureAwait(false);

            case "remove":
            {
                // Accept both "remove <name> [--force]" and "remove --force <name>"
                var remainingArgs = args.Skip(1).ToList();
                var force = remainingArgs.Remove("--force");
                if (remainingArgs.Count == 0)
                {
                    this.PrintUsage(context);
                    return CommandResult.Continue;
                }
                return await this.ExecuteRemoveAsync(context, remainingArgs[0], force, cancellationToken).ConfigureAwait(false);
            }

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

            case "search":
                if (args.Count < 2)
                {
                    this.PrintUsage(context);
                    return CommandResult.Continue;
                }
                return await this.ExecuteSearchAsync(context, string.Join(" ", args.Skip(1)), cancellationToken).ConfigureAwait(false);

            case "refresh":
            {
                var refreshName = args.Count >= 2 ? args[1] : null;
                return await this.ExecuteRefreshAsync(context, refreshName, cancellationToken).ConfigureAwait(false);
            }

            default:
                context.Console.MarkupLine(Theme.WarnMarkup($"Unknown subcommand '{subcommand}'."));
                this.PrintUsage(context);
                return CommandResult.Continue;
        }
    }

    private Task<CommandResult> ExecuteListAsync(CommandContext context)
    {
        var manager = this.BuildManager(context);
        var marketplaces = manager.List();

        if (marketplaces.Count == 0)
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                "No marketplaces added. Use /marketplace add <source>."));
            return Task.FromResult(CommandResult.Continue);
        }

        context.Console.MarkupLine(Theme.BoldMarkup("Marketplaces"));
        var grid = new Grid().AddColumn().AddColumn().AddColumn().AddColumn();
        foreach (var (name, entry, blockReason) in marketplaces)
        {
            var sourceSummary = GetSourceSummary(entry.Source);
            var statusMarkup = blockReason is not null
                ? Theme.ErrorMarkup($"[blocked: {blockReason}]")
                : string.Empty;
            grid.AddRow(
                Theme.AccentMarkup(name),
                Theme.DimMarkup(sourceSummary),
                Theme.DimMarkup(entry.LastUpdated),
                statusMarkup);
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
        var manager = this.BuildManager(context);
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

    private async Task<CommandResult> ExecuteRemoveAsync(
        CommandContext context,
        string name,
        bool force,
        CancellationToken ct)
    {
        var manager = this.BuildManager(context);
        var (ok, message, dependents) = manager.Remove(name, force);

        if (ok)
        {
            context.Console.MarkupLine(Theme.SuccessMarkup(message));
        }
        else
        {
            context.Console.MarkupLine(Theme.ErrorMarkup(message));
            if (dependents.Count > 0)
            {
                context.Console.MarkupLine(Theme.DimMarkup(
                    $"Dependent plugins: {string.Join(", ", dependents)}"));
            }
        }

        return CommandResult.Continue;
    }

    private async Task<CommandResult> ExecuteBrowseAsync(
        CommandContext context,
        string name,
        CancellationToken ct)
    {
        var manager = this.BuildManager(context);
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
        var manager = this.BuildManager(context);
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

    private async Task<CommandResult> ExecuteSearchAsync(
        CommandContext context,
        string query,
        CancellationToken ct)
    {
        var manager = this.BuildManager(context);
        var results = await manager.SearchAsync(query, ct).ConfigureAwait(false);

        if (results.Count == 0)
        {
            context.Console.MarkupLine(Theme.DimMarkup($"No plugins matching '{query}'."));
            return CommandResult.Continue;
        }

        var grid = new Grid().AddColumn().AddColumn().AddColumn().AddColumn().AddColumn();
        foreach (var result in results)
        {
            var version = result.Entry.Version ?? string.Empty;
            var description = result.Entry.Description ?? string.Empty;
            var installedMarker = result.IsInstalled ? Theme.SuccessMarkup("installed") : string.Empty;
            grid.AddRow(
                Theme.AccentMarkup(result.Entry.Name),
                Theme.DimMarkup(result.MarketplaceName),
                Theme.DimMarkup(version),
                Theme.DimMarkup(description),
                installedMarker);
        }

        context.Console.Write(grid);
        context.Console.WriteLine();
        return CommandResult.Continue;
    }

    private async Task<CommandResult> ExecuteRefreshAsync(
        CommandContext context,
        string? name,
        CancellationToken ct)
    {
        var manager = this.BuildManager(context);
        var results = await manager.RefreshAsync(name, ct).ConfigureAwait(false);

        if (results.Count == 0)
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                name is not null
                    ? $"No marketplace named '{name}'."
                    : "No marketplaces configured."));
            return CommandResult.Continue;
        }

        foreach (var result in results)
        {
            if (!result.Ok)
            {
                context.Console.MarkupLine(Theme.ErrorMarkup(
                    $"{result.Name}: refresh failed — {result.Error}"));
                continue;
            }

            var diff = result.Diff!;
            if (diff.Added.Count == 0 && diff.Removed.Count == 0 && diff.VersionChanged.Count == 0)
            {
                context.Console.MarkupLine(Theme.DimMarkup($"{result.Name}: up to date."));
            }
            else
            {
                context.Console.MarkupLine(Theme.SuccessMarkup($"{result.Name}: refreshed."));
                if (diff.Added.Count > 0)
                {
                    context.Console.MarkupLine(Theme.DimMarkup($"  Added: {string.Join(", ", diff.Added)}"));
                }

                if (diff.Removed.Count > 0)
                {
                    context.Console.MarkupLine(Theme.DimMarkup($"  Removed: {string.Join(", ", diff.Removed)}"));
                }

                if (diff.VersionChanged.Count > 0)
                {
                    foreach (var (pName, oldVer, newVer) in diff.VersionChanged)
                    {
                        context.Console.MarkupLine(Theme.DimMarkup($"  Updated: {pName} {oldVer} → {newVer}"));
                    }
                }
            }
        }

        return CommandResult.Continue;
    }

    private void PrintUsage(CommandContext context)
    {
        context.Console.MarkupLine(Theme.WarnMarkup(
            "Usage: /marketplace [add <source> | list | remove <name> [--force] | browse <name> | install <plugin> <marketplace> | search <query> | refresh [<name>]]"));
    }

    /// <summary>
    /// Present the marketplace action picker (title <c>Marketplace action</c>, stable option ids
    /// <c>list/add/remove/browse/install/search/refresh</c>) through the host-neutral prompt
    /// surface. Returns the chosen action id, or <c>null</c> when the user dismisses the prompt.
    /// </summary>
    internal async Task<string?> ChooseActionAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        var response = await context.Prompts.RequestAsync(
            UiPromptRequest.Select("Marketplace action", new[]
            {
                new UiPromptOption("list", "List marketplaces"),
                new UiPromptOption("add", "Add a marketplace"),
                new UiPromptOption("remove", "Remove a marketplace"),
                new UiPromptOption("browse", "Browse a marketplace"),
                new UiPromptOption("install", "Install a plugin from a marketplace"),
                new UiPromptOption("search", "Search across marketplaces"),
                new UiPromptOption("refresh", "Refresh marketplaces"),
            }),
            cancellationToken).ConfigureAwait(false);

        return response.Cancelled || response.SelectedIds.Length == 0 ? null : response.SelectedIds[0];
    }

    private MarketplaceManager BuildManager(CommandContext? context = null)
    {
        var pluginsDir = this.ResolveUserPluginsDir();
        if (context?.PluginState is { } state)
        {
            return new MarketplaceManager(pluginsDir, state);
        }
        return new MarketplaceManager(pluginsDir);
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
