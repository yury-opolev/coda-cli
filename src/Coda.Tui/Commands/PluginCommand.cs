using Coda.Tui.Plugins;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Manages plugins: install, remove, list, info, enable, disable, update, and prune.
/// </summary>
public sealed class PluginCommand : ISlashCommand
{
    private readonly string? userPluginsDirOverride;
    private readonly PluginUpdater? updaterOverride;
    private readonly PluginTrustStore? trustStoreOverride;

    /// <summary>Production constructor — resolves paths from the environment.</summary>
    public PluginCommand() : this(null, null, null)
    {
    }

    /// <summary>Creates the command with an explicit plugins directory (for testing).</summary>
    public PluginCommand(string? userPluginsDirOverride) : this(userPluginsDirOverride, null, null)
    {
    }

    /// <summary>Creates the command with explicit overrides including the trust store (for testing).</summary>
    public PluginCommand(
        string? userPluginsDirOverride = null,
        PluginUpdater? updaterOverride = null,
        PluginTrustStore? trustStoreOverride = null)
    {
        this.userPluginsDirOverride = userPluginsDirOverride;
        this.updaterOverride = updaterOverride;
        this.trustStoreOverride = trustStoreOverride;
    }

    public string Name => "plugin";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Manage plugins: install, remove, enable, disable, update, prune";

    public CommandHelp Help => new(
        Usage: "/plugin [list | info <name> | install <source> | remove <name> | enable <name> | disable <name> | update <name> | prune | approve <name> | validate <path> | new <name>]",
        Description: "Manages plugins. Without a subcommand (or with 'list'), lists installed plugins. " +
            "'info' shows detailed information about a plugin including components and trust state. " +
            "'install' accepts a local directory path or a git URL (http/https/git@). " +
            "'remove' uninstalls a plugin. " +
            "'enable'/'disable' toggle whether a plugin is active. " +
            "'update' fetches the latest version of a git-installed plugin. " +
            "'prune' reports dependency-only plugins that are no longer required. " +
            "'approve' re-runs the per-class approval prompt for an already-installed plugin. " +
            "'validate' parses and checks a plugin manifest. " +
            "'new' scaffolds a new plugin directory.",
        Options:
        [
            ("list", "List all installed plugins (default when no subcommand is given)."),
            ("info <name>", "Show detailed information about a plugin including trust state and config."),
            ("install <source>", "Install a plugin from a local directory path or a git URL."),
            ("remove <name>", "Uninstall the named plugin from the user plugins directory."),
            ("enable <name>", "Enable a plugin (re-enable one that was disabled)."),
            ("disable <name>", "Disable a plugin without removing it."),
            ("update <name>", "Update a git-installed plugin to the latest version."),
            ("prune", "List dependency-only plugins that are no longer required by anything."),
            ("approve <name>", "Re-run the per-class approval prompt for a plugin installed with withheld components."),
            ("validate <path>", "Parse and validate the plugin.json at <path> (or <path>/.claude-plugin/plugin.json)."),
            ("new <name>", "Scaffold a new plugin at <cwd>/.coda/plugins/<name>/plugin.json."),
        ],
        Examples:
        [
            "/plugin",
            "/plugin list",
            "/plugin info my-plugin",
            "/plugin install ./my-plugin",
            "/plugin install https://github.com/example/coda-plugin.git",
            "/plugin remove my-plugin",
            "/plugin enable my-plugin",
            "/plugin disable my-plugin",
            "/plugin update my-plugin",
            "/plugin prune",
            "/plugin approve my-plugin",
            "/plugin validate ./my-plugin",
            "/plugin new my-plugin",
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

            case "info":
                if (args.Count < 2)
                {
                    context.Console.MarkupLine(Theme.WarnMarkup("Usage: /plugin info <name>"));
                    return CommandResult.Continue;
                }
                return this.ExecuteInfo(context, args[1]);

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

            case "approve":
                if (args.Count < 2)
                {
                    context.Console.MarkupLine(Theme.WarnMarkup("Usage: /plugin approve <name>"));
                    return CommandResult.Continue;
                }
                return await this.ExecuteApproveAsync(context, args[1], cancellationToken).ConfigureAwait(false);

            case "validate":
                if (args.Count < 2)
                {
                    context.Console.MarkupLine(Theme.WarnMarkup("Usage: /plugin validate <path>"));
                    return CommandResult.Continue;
                }
                return ExecuteValidate(context, args[1]);

            case "new":
                if (args.Count < 2)
                {
                    context.Console.MarkupLine(Theme.WarnMarkup("Usage: /plugin new <name>"));
                    return CommandResult.Continue;
                }
                return ExecuteNew(context, args[1]);

            default:
                context.Console.MarkupLine(Theme.WarnMarkup(
                    $"Unknown subcommand '{subcommand}'. Usage: /plugin [list|info|install|remove|enable|disable|update|prune|approve|validate|new]"));
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

        // Record install metadata, collect userConfig, and prompt for per-class approval.
        await this.PostInstallAsync(context, userPluginsDir, source, ct).ConfigureAwait(false);

        return CommandResult.Continue;
    }

    /// <summary>
    /// Shows detailed information about a named plugin: components, origin, pinned commit,
    /// enabled and trust state, and <c>userConfig</c> values with secrets redacted.
    /// </summary>
    private CommandResult ExecuteInfo(CommandContext context, string name)
    {
        // Use the same user coda dir as the plugins dir so the injected dir is respected in tests.
        var userCodaDir = this.ResolveUserCodaDirForLoad();
        var plugins = PluginLoader.Load(context.Session.WorkingDirectory, userCodaDir: userCodaDir, stateStore: null);
        var plugin = plugins.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (plugin is null)
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Plugin '{name}' is not installed. Use /plugin list to see installed plugins."));
            return CommandResult.Continue;
        }

        var stateStore = this.ResolveStateStore(context);
        var installInfo = stateStore?.GetInstalledInfo(plugin.Name);
        var trustStore = this.ResolveTrustStore();
        var hash = PluginContentHash.Compute(plugin);
        var approvedClasses = trustStore.GetApprovedClasses(hash);

        context.Console.MarkupLine(Theme.BoldMarkup(plugin.Name));
        var grid = new Grid().AddColumn().AddColumn();

        // Version
        grid.AddRow(Theme.DimMarkup("Version"), $"v{plugin.Version}");

        // Description
        if (!string.IsNullOrWhiteSpace(plugin.Manifest?.Description ?? plugin.Description))
        {
            grid.AddRow(Theme.DimMarkup("Description"), plugin.Manifest?.Description ?? plugin.Description);
        }

        // Components
        var inventory = PluginInventory.FromManifest(plugin.Manifest, plugin.Directory);
        grid.AddRow(Theme.DimMarkup("Components"), inventory.IsEmpty ? "(none)" : inventory.ToDisplayString());

        // Origin
        var origin = installInfo is null
            ? "local"
            : installInfo.Source == "git" && installInfo.GitUrl is not null
                ? $"git: {installInfo.GitUrl}"
                : installInfo.Marketplace is not null
                    ? $"marketplace: {installInfo.Marketplace}"
                    : $"local: {plugin.Directory}";
        grid.AddRow(Theme.DimMarkup("Origin"), origin);

        // Pinned commit
        if (installInfo?.Commit is { } commit)
        {
            grid.AddRow(Theme.DimMarkup("Pinned commit"), commit);
        }

        // Enabled state
        var isEnabled = stateStore?.IsEnabled(plugin.Name, plugin.Manifest?.DefaultEnabled ?? true) ?? plugin.IsEnabled;
        grid.AddRow(Theme.DimMarkup("Enabled"), isEnabled ? Theme.SuccessMarkup("Yes") : Theme.WarnMarkup("No"));

        // Workspace trust (project-scoped plugins only)
        var workingDir = context.Session.WorkingDirectory;
        var projectPluginsPath = Path.GetFullPath(Path.Combine(workingDir, ".coda", "plugins"))
            + Path.DirectorySeparatorChar;
        var pluginDirFull = Path.GetFullPath(plugin.Directory) + Path.DirectorySeparatorChar;
        var isProjectScoped = pluginDirFull.StartsWith(projectPluginsPath, StringComparison.OrdinalIgnoreCase);
        if (isProjectScoped)
        {
            var wsTrusted = trustStore.IsWorkspaceTrusted(workingDir);
            grid.AddRow(Theme.DimMarkup("Workspace trust"), wsTrusted ? Theme.SuccessMarkup("Trusted") : Theme.WarnMarkup("Untrusted"));
        }

        // Per-class trust state
        if (!inventory.IsEmpty)
        {
            var hasRecord = trustStore.HasApprovalRecord(hash);
            if (!hasRecord)
            {
                grid.AddRow(Theme.DimMarkup("Class approvals"), Theme.DimMarkup("(legacy install — all implicitly approved)"));
            }
            else
            {
                var classLines = new List<string>();
                foreach (var cls in inventory.PresentClasses.OrderBy(c => c.ToString()))
                {
                    var approved = approvedClasses.Contains(cls);
                    classLines.Add(approved
                        ? $"{cls}: {Theme.SuccessMarkup("approved")}"
                        : $"{cls}: {Theme.WarnMarkup("refused")}");
                }

                grid.AddRow(Theme.DimMarkup("Class approvals"), string.Join(", ", classLines));
            }
        }

        context.Console.Write(grid);

        // userConfig values — secrets redacted
        if (plugin.Manifest?.UserConfig is { Count: > 0 } userConfigFields && stateStore is not null)
        {
            var configValues = stateStore.GetPluginConfig(plugin.Name);
            context.Console.WriteLine();
            context.Console.MarkupLine(Theme.DimMarkup("Configuration:"));
            var cfgGrid = new Grid().AddColumn().AddColumn();
            foreach (var field in userConfigFields)
            {
                string displayValue;
                if (field.Type == UserConfigFieldType.Secret)
                {
                    displayValue = Theme.DimMarkup("***");
                }
                else if (configValues.TryGetValue(field.Key, out var val))
                {
                    displayValue = val;
                }
                else
                {
                    displayValue = Theme.DimMarkup("(not set)");
                }

                cfgGrid.AddRow(Theme.DimMarkup(field.Key), displayValue);
            }

            context.Console.Write(cfgGrid);
        }

        context.Console.WriteLine();
        return CommandResult.Continue;
    }

    /// <summary>
    /// After a successful install: record install metadata, prompt for per-class approval,
    /// and call <see cref="PluginUserConfigService.ConfigureAsync"/> for any declared
    /// <c>userConfig</c> fields so that defaults are persisted immediately.
    /// </summary>
    private async Task PostInstallAsync(
        CommandContext context, string userPluginsDir, string source, CancellationToken ct)
    {
        var stateStore = this.ResolveStateStore(context);
        if (stateStore is null)
        {
            return;
        }

        var trustStore = this.ResolveTrustStore();
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

                    // Show inventory and prompt for per-class approval.
                    var inventory = PluginInventory.FromManifest(manifest, subDir);
                    // Compute hash over the full component surface so in-place edits re-prompt.
                    var pluginInfoForHash = new PluginInfo(
                        manifest.Name, manifest.Version, manifest.Description, subDir)
                        { Manifest = manifest };
                    var hash = PluginContentHash.Compute(pluginInfoForHash);
                    await this.RecordApprovalAsync(context, manifest.Name, hash, inventory, ct)
                        .ConfigureAwait(false);

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

    /// <summary>
    /// Shows the component inventory and prompts interactively for per-class approval.
    /// In unattended contexts, stores an empty approval set and reports withheld classes.
    /// </summary>
    private async Task RecordApprovalAsync(
        CommandContext context,
        string pluginName,
        string hash,
        PluginInventory inventory,
        CancellationToken ct)
    {
        var trustStore = this.ResolveTrustStore();

        if (inventory.IsEmpty)
        {
            // No components to approve; record an empty approval set silently.
            trustStore.SetApprovedClasses(hash, []);
            return;
        }

        // Display component inventory.
        context.Console.WriteLine();
        context.Console.MarkupLine(
            Theme.BoldMarkup($"Plugin '{pluginName}' provides the following components:"));
        context.Console.MarkupLine(inventory.ToDisplayString());
        context.Console.WriteLine();

        var approvedClasses = new List<PluginComponentClass>();
        var withheldClasses = new List<PluginComponentClass>();

        if (!context.Prompts.IsInteractive)
        {
            // Unattended: deny everything, report withheld.
            withheldClasses.AddRange(inventory.PresentClasses);
        }
        else
        {
            // Interactive: prompt per present class.
            foreach (var cls in inventory.PresentClasses.OrderBy(c => c.ToString()))
            {
                var description = cls switch
                {
                    PluginComponentClass.Skill => "Skills are prompts loaded into your context.",
                    PluginComponentClass.Hook => "Hooks run as subprocesses on every conversation turn.",
                    PluginComponentClass.McpServer => "MCP servers are long-lived processes that expose tools.",
                    PluginComponentClass.Subagent => "Subagents can be invoked by the model as sub-conversations.",
                    _ => string.Empty
                };

                context.Console.MarkupLine(Theme.DimMarkup(description));
                var answer = await context.Prompts.RequestAsync(
                    UiPromptRequest.Confirm($"Approve {cls} components?", true),
                    ct).ConfigureAwait(false);

                if (!answer.Cancelled && answer.SelectedIds.Contains("yes"))
                {
                    approvedClasses.Add(cls);
                }
                else
                {
                    withheldClasses.Add(cls);
                }
            }
        }

        trustStore.SetApprovedClasses(hash, approvedClasses);

        if (withheldClasses.Count > 0)
        {
            var withheldNames = string.Join(", ", withheldClasses.Select(c => c.ToString().ToLowerInvariant()));
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Plugin installed without activating withheld components: {withheldNames}. " +
                "Approve them interactively to activate."));
        }
    }

    /// <summary>
    /// Re-shows the component inventory for an installed plugin and records a fresh
    /// per-class approval decision. Useful when a plugin was installed unattended
    /// (all classes withheld) and the user wants to grant approval interactively.
    /// </summary>
    private async Task<CommandResult> ExecuteApproveAsync(
        CommandContext context,
        string name,
        CancellationToken ct)
    {
        var userPluginsDir = this.ResolveUserPluginsDir();
        var pluginDir = Path.Combine(userPluginsDir, name);
        var pluginJsonPath = Path.Combine(pluginDir, "plugin.json");

        if (!File.Exists(pluginJsonPath))
        {
            context.Console.MarkupLine(Theme.WarnMarkup($"Plugin '{name}' is not installed."));
            return CommandResult.Continue;
        }

        string json;
        PluginManifest manifest;
        try
        {
            json = await File.ReadAllTextAsync(pluginJsonPath, ct).ConfigureAwait(false);
            manifest = PluginManifestParser.Parse(json, pluginDir);
        }
        catch (Exception ex)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Failed to read plugin manifest: {ex.Message}"));
            return CommandResult.Continue;
        }

        var pluginInfo = new PluginInfo(manifest.Name, manifest.Version, manifest.Description, pluginDir)
            { Manifest = manifest };
        var hash = PluginContentHash.Compute(pluginInfo);
        var inventory = PluginInventory.FromManifest(manifest, pluginDir);

        await this.RecordApprovalAsync(context, name, hash, inventory, ct).ConfigureAwait(false);
        return CommandResult.Continue;
    }

    /// <summary>
    /// Parses and validates a plugin manifest at <paramref name="inputPath"/>, which may be a
    /// <c>plugin.json</c> file, a Coda plugin directory (containing <c>plugin.json</c>), or a foreign
    /// plugin directory (containing <c>.claude-plugin/plugin.json</c>).
    /// </summary>
    private static CommandResult ExecuteValidate(CommandContext context, string inputPath)
    {
        var manifestPath = ResolveManifestPath(inputPath);
        if (manifestPath is null)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup(
                $"No plugin.json found at '{inputPath}' (looked for plugin.json and .claude-plugin/plugin.json)."));
            return CommandResult.Continue;
        }

        string json;
        try
        {
            json = File.ReadAllText(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Cannot read manifest: {ex.Message}"));
            return CommandResult.Continue;
        }

        context.Console.MarkupLine(Theme.BoldMarkup($"Validating {manifestPath}"));
        var grid = new Grid().AddColumn().AddColumn();

        try
        {
            var manifest = PluginManifestParser.Parse(json, Path.GetDirectoryName(manifestPath)!);
            grid.AddRow(Theme.DimMarkup("name"), Theme.AccentMarkup(manifest.Name));
            grid.AddRow(Theme.DimMarkup("version"), $"v{manifest.Version}");
            if (!string.IsNullOrWhiteSpace(manifest.Description))
            {
                grid.AddRow(Theme.DimMarkup("description"), manifest.Description);
            }

            var inventory = PluginInventory.FromManifest(manifest, Path.GetDirectoryName(manifestPath)!);
            grid.AddRow(Theme.DimMarkup("components"), inventory.IsEmpty ? "(none)" : inventory.ToDisplayString());
            grid.AddRow(Theme.DimMarkup("problems"), Theme.SuccessMarkup("none"));
            context.Console.Write(grid);
        }
        catch (PluginManifestPathException ex)
        {
            grid.AddRow(Theme.DimMarkup("problems"), Theme.ErrorMarkup($"path containment: {ex.Message}"));
            context.Console.Write(grid);
        }
        catch (PluginManifestParseException ex)
        {
            grid.AddRow(Theme.DimMarkup("problems"), Theme.WarnMarkup(ex.Message));
            context.Console.Write(grid);
        }

        context.Console.WriteLine();
        return CommandResult.Continue;
    }

    private static string? ResolveManifestPath(string inputPath)
    {
        if (File.Exists(inputPath))
        {
            return inputPath;
        }

        if (Directory.Exists(inputPath))
        {
            var direct = Path.Combine(inputPath, "plugin.json");
            if (File.Exists(direct))
            {
                return direct;
            }

            var foreign = Path.Combine(inputPath, ".claude-plugin", "plugin.json");
            if (File.Exists(foreign))
            {
                return foreign;
            }
        }

        return null;
    }

    /// <summary>Scaffolds a new plugin at <c>&lt;cwd&gt;/.coda/plugins/&lt;name&gt;/plugin.json</c>.</summary>
    private static CommandResult ExecuteNew(CommandContext context, string name)
    {
        if (!PluginInstaller.IsValidPluginName(name) || name.Contains("..", StringComparison.Ordinal))
        {
            context.Console.MarkupLine(Theme.ErrorMarkup(
                $"Invalid plugin name '{name}': must not contain path separators, '..', or invalid characters."));
            return CommandResult.Continue;
        }

        var pluginDir = Path.Combine(context.Session.WorkingDirectory, ".coda", "plugins", name);
        if (Directory.Exists(pluginDir))
        {
            context.Console.MarkupLine(Theme.WarnMarkup($"Plugin '{name}' already exists at {pluginDir}"));
            return CommandResult.Continue;
        }

        try
        {
            Directory.CreateDirectory(pluginDir);
            var manifestPath = Path.Combine(pluginDir, "plugin.json");
            var nl = Environment.NewLine;
            var template =
                "{" + nl +
                $"  \"name\": \"{name}\"," + nl +
                "  \"version\": \"0.1.0\"," + nl +
                "  \"description\": \"Describe your plugin here.\"" + nl +
                "}" + nl;
            File.WriteAllText(manifestPath, template);
            context.Console.MarkupLine(Theme.SuccessMarkup($"Created {manifestPath}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Failed to create plugin: {ex.Message}"));
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

    private PluginStateStore? ResolveStateStore(CommandContext context)
    {
        if (context.PluginState is not null)
        {
            return context.PluginState;
        }

        var codaDir = this.ResolveUserCodaDir();
        return new PluginStateStore(codaDir);
    }

    private PluginTrustStore ResolveTrustStore()
    {
        if (this.trustStoreOverride is not null)
        {
            return this.trustStoreOverride;
        }

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new PluginTrustStore(homeDir);
    }

    /// <summary>
    /// Derives the user <c>.coda</c> directory from <see cref="userPluginsDirOverride"/> (its parent),
    /// so that <see cref="PluginLoader.Load"/> resolves plugins from the same base as install.
    /// Returns <see langword="null"/> when no override is set, letting the loader use its default.
    /// </summary>
    private string? ResolveUserCodaDirForLoad()
    {
        if (this.userPluginsDirOverride is not null)
        {
            return Path.GetDirectoryName(Path.GetFullPath(this.userPluginsDirOverride));
        }

        return null;
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
