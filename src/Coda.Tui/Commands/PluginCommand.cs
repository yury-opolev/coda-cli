using Coda.Tui.Plugins;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Manages plugins: install, remove, list, enable, disable, update, and prune.
/// </summary>
public sealed class PluginCommand : ISlashCommand
{
    private readonly string? userPluginsDirOverride;
    private readonly PluginUpdater? updaterOverride;

    /// <summary>Production constructor — resolves paths from the environment.</summary>
    public PluginCommand() : this(null, null)
    {
    }

    /// <summary>Creates the command with an explicit plugins directory (for testing).</summary>
    public PluginCommand(string? userPluginsDirOverride) : this(userPluginsDirOverride, null)
    {
    }

    /// <summary>Creates the command with an explicit plugins directory and updater (for testing).</summary>
    public PluginCommand(string? userPluginsDirOverride = null, PluginUpdater? updaterOverride = null)
    {
        this.userPluginsDirOverride = userPluginsDirOverride;
        this.updaterOverride = updaterOverride;
    }

    public string Name => "plugin";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Manage plugins: install, remove, enable, disable, update, prune";

    public CommandHelp Help => new(
        Usage: "/plugin [list | install <source> | remove <name> | enable <name> | disable <name> | update <name> | prune]",
        Description: "Manages plugins. Without a subcommand (or with 'list'), lists installed plugins. " +
            "'install' accepts a local directory path or a git URL (http/https/git@). " +
            "'remove' uninstalls a plugin. " +
            "'enable'/'disable' toggle whether a plugin is active. " +
            "'update' fetches the latest version of a git-installed plugin. " +
            "'prune' reports dependency-only plugins that are no longer required.",
        Options:
        [
            ("list", "List all installed plugins (default when no subcommand is given)."),
            ("install <source>", "Install a plugin from a local directory path or a git URL."),
            ("remove <name>", "Uninstall the named plugin from the user plugins directory."),
            ("enable <name>", "Enable a plugin (re-enable one that was disabled)."),
            ("disable <name>", "Disable a plugin without removing it."),
            ("update <name>", "Update a git-installed plugin to the latest version."),
            ("prune", "List dependency-only plugins that are no longer required by anything."),
        ],
        Examples:
        [
            "/plugin",
            "/plugin list",
            "/plugin install ./my-plugin",
            "/plugin install https://github.com/example/coda-plugin.git",
            "/plugin remove my-plugin",
            "/plugin enable my-plugin",
            "/plugin disable my-plugin",
            "/plugin update my-plugin",
            "/plugin prune",
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

            case "enable":
                if (args.Count < 2)
                {
                    context.Console.MarkupLine(Theme.WarnMarkup("Usage: /plugin enable <name>"));
                    return CommandResult.Continue;
                }
                return this.ExecuteSetEnabled(context, args[1], enabled: true);

            case "disable":
                if (args.Count < 2)
                {
                    context.Console.MarkupLine(Theme.WarnMarkup("Usage: /plugin disable <name>"));
                    return CommandResult.Continue;
                }
                return this.ExecuteSetEnabled(context, args[1], enabled: false);

            case "update":
                if (args.Count < 2)
                {
                    context.Console.MarkupLine(Theme.WarnMarkup("Usage: /plugin update <name>"));
                    return CommandResult.Continue;
                }
                return await this.ExecuteUpdateAsync(context, args[1], cancellationToken).ConfigureAwait(false);

            case "prune":
                return this.ExecutePrune(context);

            default:
                context.Console.MarkupLine(Theme.WarnMarkup(
                    $"Unknown subcommand '{subcommand}'. Usage: /plugin [list|install|remove|enable|disable|update|prune]"));
                return CommandResult.Continue;
        }
    }

    /// <summary>
    /// Present the plugin action picker through the host-neutral prompt surface.
    /// Returns the chosen action id, or <c>null</c> when the user dismisses the prompt.
    /// </summary>
    internal async Task<string?> ChooseActionAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        var response = await context.Prompts.RequestAsync(
            UiPromptRequest.Select("Plugin action", new[]
            {
                new UiPromptOption("list", "List installed plugins"),
                new UiPromptOption("install", "Install a plugin"),
                new UiPromptOption("remove", "Remove a plugin"),
                new UiPromptOption("enable", "Enable a plugin"),
                new UiPromptOption("disable", "Disable a plugin"),
                new UiPromptOption("update", "Update a plugin"),
                new UiPromptOption("prune", "List pruneable dependency plugins"),
            }),
            cancellationToken).ConfigureAwait(false);

        return response.Cancelled || response.SelectedIds.Length == 0 ? null : response.SelectedIds[0];
    }

    private CommandResult ExecuteList(CommandContext context)
    {
        var plugins = PluginLoader.Load(context.Session.WorkingDirectory, stateStore: context.PluginState);

        if (plugins.Count == 0)
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                "No plugins installed. Use /plugin install <path-or-git-url> to add one."));
            return CommandResult.Continue;
        }

        context.Console.MarkupLine(Theme.BoldMarkup("Plugins"));
        var grid = new Grid().AddColumn().AddColumn().AddColumn().AddColumn();
        foreach (var plugin in plugins)
        {
            var versionText = $"v{plugin.Version}";
            var description = string.IsNullOrWhiteSpace(plugin.Description)
                ? string.Empty
                : plugin.Description;
            var status = plugin.IsEnabled ? string.Empty : Theme.WarnMarkup("[disabled]");
            grid.AddRow(
                Theme.AccentMarkup(plugin.Name),
                Theme.DimMarkup(versionText),
                Theme.DimMarkup(description),
                status);
        }

        context.Console.Write(grid);
        context.Console.WriteLine();
        return CommandResult.Continue;
    }

    private CommandResult ExecuteSetEnabled(CommandContext context, string name, bool enabled)
    {
        var stateStore = this.ResolveStateStore(context);
        if (stateStore is null)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup("Plugin state store is not available."));
            return CommandResult.Continue;
        }

        // Verify the plugin is known (either in user or project plugins)
        var plugins = PluginLoader.Load(context.Session.WorkingDirectory);
        var plugin = plugins.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (plugin is null)
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Plugin '{name}' is not installed. Use /plugin list to see installed plugins."));
            return CommandResult.Continue;
        }

        stateStore.SetEnabled(name, enabled);
        var verb = enabled ? "enabled" : "disabled";
        context.Console.MarkupLine(Theme.SuccessMarkup($"Plugin '{name}' {verb}."));
        return CommandResult.Continue;
    }

    private async Task<CommandResult> ExecuteUpdateAsync(
        CommandContext context, string name, CancellationToken ct)
    {
        var stateStore = this.ResolveStateStore(context);
        if (stateStore is null)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup("Plugin state store is not available."));
            return CommandResult.Continue;
        }

        var installInfo = stateStore.GetInstalledInfo(name);
        if (installInfo is null)
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"No install record found for '{name}'. Re-install the plugin to register it."));
            return CommandResult.Continue;
        }

        var codaDir = this.ResolveUserCodaDir();
        var updater = this.updaterOverride ?? new PluginUpdater(codaDir);

        // Locate the plugin directory (user plugins)
        var pluginDir = Path.Combine(this.ResolveUserPluginsDir(), name);

        var result = await updater.UpdateAsync(pluginDir, installInfo, ct).ConfigureAwait(false);
        if (result.Ok)
        {
            context.Console.MarkupLine(Theme.SuccessMarkup(result.Message));
        }
        else
        {
            context.Console.MarkupLine(Theme.ErrorMarkup(result.Message));
        }

        return CommandResult.Continue;
    }

    private CommandResult ExecutePrune(CommandContext context)
    {
        var stateStore = this.ResolveStateStore(context);
        if (stateStore is null)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup("Plugin state store is not available."));
            return CommandResult.Continue;
        }

        var plugins = PluginLoader.Load(context.Session.WorkingDirectory, stateStore: stateStore);
        var pruneable = PluginDependencyResolver.FindPruneable(plugins, stateStore);

        if (pruneable.Count == 0)
        {
            context.Console.MarkupLine(Theme.DimMarkup("No pruneable dependency plugins found."));
            return CommandResult.Continue;
        }

        context.Console.MarkupLine(Theme.BoldMarkup("Pruneable dependency plugins:"));
        foreach (var pluginName in pruneable)
        {
            context.Console.MarkupLine($"  {Theme.WarnMarkup(pluginName)}");
        }

        context.Console.MarkupLine(Theme.DimMarkup(
            "Use /plugin remove <name> to remove a pruneable plugin."));
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
            var (installOk, installMsg, _) = await PluginInstaller.InstallFromGitAsync(userPluginsDir, source, null, ct)
                .ConfigureAwait(false);
            result = (installOk, installMsg);
        }
        else
        {
            result = await PluginInstaller.InstallFromDirectoryAsync(userPluginsDir, source, ct)
                .ConfigureAwait(false);
        }

        if (!result.ok)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup(result.message));
            return CommandResult.Continue;
        }

        context.Console.MarkupLine(Theme.SuccessMarkup(result.message));

        // Record install metadata and call ConfigureAsync for any declared userConfig fields.
        await this.PostInstallAsync(context, userPluginsDir, source, ct).ConfigureAwait(false);

        return CommandResult.Continue;
    }

    /// <summary>
    /// After a successful install: record install metadata in the state store and call
    /// <see cref="PluginUserConfigService.ConfigureAsync"/> for any declared <c>userConfig</c>
    /// fields so that defaults are persisted immediately.
    /// </summary>
    private async Task PostInstallAsync(
        CommandContext context, string userPluginsDir, string source, CancellationToken ct)
    {
        var stateStore = this.ResolveStateStore(context);
        if (stateStore is null)
        {
            return;
        }

        // Find the just-installed plugin by scanning the plugins directory for manifests we
        // can parse — the plugin name is in the manifest.
        var pluginSource = IsGitUrl(source) ? "git" : "local";
        var gitUrl = IsGitUrl(source) ? source : null;

        foreach (var subDir in Directory.EnumerateDirectories(userPluginsDir))
        {
            var pluginJsonPath = Path.Combine(subDir, "plugin.json");
            if (!File.Exists(pluginJsonPath))
            {
                continue;
            }

            try
            {
                var json = await File.ReadAllTextAsync(pluginJsonPath, ct).ConfigureAwait(false);
                var manifest = PluginManifestParser.Parse(json, subDir);

                // Only process plugins that have no recorded install info yet (just installed).
                if (stateStore.GetInstalledInfo(manifest.Name) is null)
                {
                    stateStore.SetInstalledInfo(manifest.Name, new PluginInstallInfo(
                        manifest.Version, pluginSource, gitUrl, null, DateTimeOffset.UtcNow));

                    if (manifest.UserConfig.Count > 0 && context.CredentialStore is not null)
                    {
                        var configResult = await PluginUserConfigService.ConfigureAsync(
                            manifest.Name,
                            manifest.UserConfig,
                            context.Prompts,
                            context.CredentialStore,
                            stateStore,
                            ct).ConfigureAwait(false);

                        if (!configResult.Ok && configResult.DisabledReason is not null)
                        {
                            context.Console.MarkupLine(Theme.WarnMarkup(
                                $"Plugin '{manifest.Name}' requires configuration: {configResult.DisabledReason}"));
                        }
                    }

                    // Report any unmet dependencies.
                    var allPlugins = PluginLoader.Load(context.Session.WorkingDirectory, stateStore: stateStore);
                    var unmet = PluginDependencyResolver.FindUnmet(manifest, allPlugins);
                    if (unmet.Count > 0)
                    {
                        context.Console.MarkupLine(Theme.WarnMarkup(
                            $"Plugin '{manifest.Name}' has unmet dependencies:"));
                        foreach (var dep in unmet)
                        {
                            context.Console.MarkupLine($"  {Theme.WarnMarkup(dep.Reason)}");
                        }
                    }
                }
            }
            catch
            {
                // Best-effort post-install work; ignore errors.
            }
        }
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

    private PluginStateStore? ResolveStateStore(CommandContext context)
    {
        if (context.PluginState is not null)
        {
            return context.PluginState;
        }

        var codaDir = this.ResolveUserCodaDir();
        return new PluginStateStore(codaDir);
    }

    private string ResolveUserCodaDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".coda");
    }

    private string ResolveUserPluginsDir()
    {
        if (this.userPluginsDirOverride is not null)
        {
            return this.userPluginsDirOverride;
        }

        return Path.Combine(this.ResolveUserCodaDir(), "plugins");
    }

    private static bool IsGitUrl(string source)
    {
        return source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith(".git", StringComparison.OrdinalIgnoreCase);
    }
}
