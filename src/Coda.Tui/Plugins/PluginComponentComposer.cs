using Coda.Agent.Hooks;
using Coda.Agent.OutputStyles;
using Coda.Agent.Subagents;
using Coda.Mcp;
using Coda.Tui.Skills;
using Coda.Tui.Ui.Rendering;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Plugins;

/// <summary>The result of composing all plugin-contributed components for the current session.</summary>
public sealed class PluginComposition
{
    /// <summary>All subagent definitions contributed by enabled plugins.</summary>
    public IReadOnlyList<SubagentDefinition> Agents { get; init; } = [];

    /// <summary>
    /// All hooks contributed by enabled plugins. Each hook carries a
    /// <see cref="UserHook.PluginOrigin"/> and its installation-derived <see cref="UserHook.Scope"/>.
    /// </summary>
    public IReadOnlyList<UserHook> Hooks { get; init; } = [];

    /// <summary>
    /// MCP servers contributed by enabled plugins, keyed by server name. Value includes the
    /// config and the originating plugin name for traceability. Does not include servers
    /// shadowed by user or project configuration (those are resolved at merge time by
    /// <see cref="McpConfig.LoadEntriesWithPlugins"/>).
    /// </summary>
    public IReadOnlyDictionary<string, (McpServerConfig Config, string PluginName)> McpServers { get; init; } =
        new Dictionary<string, (McpServerConfig, string)>(StringComparer.Ordinal);

    /// <summary>
    /// Output styles contributed by enabled plugins, for session-scoped resolution.
    /// These are also registered into the static <see cref="BuiltInOutputStyles"/> registry
    /// as a side-effect of <see cref="PluginComponentComposer.Compose"/>, but the list here
    /// allows serve sessions to resolve styles without sharing the process-global state.
    /// </summary>
    public IReadOnlyList<OutputStyle> OutputStyles { get; init; } = [];

    /// <summary>
    /// Themes contributed by enabled plugins. <see cref="PluginComponentComposer.Compose"/> loads
    /// them into a registry owned by the composition and publishes it via
    /// <see cref="CodaThemes.UsePluginRegistry"/>; this list is the same set, available for any
    /// session-scoped theme resolution path.
    /// </summary>
    internal IReadOnlyList<CodaTheme> Themes { get; init; } = [];

    /// <summary>
    /// Slash commands contributed by enabled, approved plugins. Each entry is a
    /// <see cref="SkillDefinition"/> parsed from a <c>.md</c> file in the plugin's commands
    /// directory. Registered into the <see cref="Coda.Tui.Repl.SlashCommandRegistry"/> by the
    /// caller (e.g. <see cref="Coda.Tui.InteractiveProgram"/>) alongside skill-derived commands.
    /// </summary>
    public IReadOnlyList<SkillDefinition> Commands { get; init; } = [];
}

/// <summary>
/// Composes all plugin-contributed components in a single pass.
/// Output styles and themes are registered into the process-global registries
/// (<see cref="Coda.Agent.OutputStyles.BuiltInOutputStyles"/> and
/// <see cref="Coda.Tui.Ui.Rendering.CodaThemes"/>) as side effects. Agents, hooks, and MCP servers
/// are returned in the <see cref="PluginComposition"/> for the caller to wire up.
/// </summary>
public static class PluginComponentComposer
{
    /// <summary>
    /// Composes all components from the supplied plugins. Safe to call at session start;
    /// a malformed component in one plugin does not prevent the rest from loading.
    /// </summary>
    /// <param name="plugins">Discovered plugins (may include disabled ones; they are skipped).</param>
    /// <param name="workingDirectory">
    /// Used to determine hook scope (project vs. user) and passed to <see cref="PluginHookLoader"/>.
    /// </param>
    /// <param name="userCodaDir">Override for the user <c>.coda</c> directory.</param>
    /// <param name="logger">Optional diagnostic logger.</param>
    /// <param name="trustStore">
    /// Optional trust store used to enforce workspace trust for project-scoped plugins and
    /// per-class approval for all plugins. When <see langword="null"/>, no trust filtering is
    /// applied (backward-compatible default used by tests and older callers).
    /// </param>
    public static PluginComposition Compose(
        IReadOnlyList<PluginInfo> plugins,
        string workingDirectory,
        string? userCodaDir = null,
        ILogger? logger = null,
        PluginTrustStore? trustStore = null)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var agents = new List<SubagentDefinition>();
        var hooks = new List<UserHook>();
        var commands = new List<SkillDefinition>();

        foreach (var plugin in plugins)
        {
            if (!plugin.IsEnabled) continue;

            // ── Trust gate ────────────────────────────────────────────────
            // When a trust store is provided, enforce workspace trust for project-scoped
            // plugins and per-class approvals for all plugins.
            PluginTrustFilter? trustFilter = null;
            if (trustStore is not null)
            {
                trustFilter = BuildTrustFilter(plugin, workingDirectory, trustStore, logger);
                if (trustFilter.BlocksAll)
                {
                    // Project-scoped plugin without workspace trust; skip entirely.
                    logger?.LogWarning(
                        "Plugin '{Plugin}': skipped — workspace is not trusted. " +
                        "Run interactively to grant workspace trust.",
                        plugin.Name);
                    continue;
                }
            }

            // Agents (M1: reject definitions that shadow a built-in type)
            if (trustFilter is null || trustFilter.IsClassAllowed(PluginComponentClass.Subagent))
            {
                try
                {
                    foreach (var definition in PluginAgentLoader.Load(plugin, logger))
                    {
                        if (BuiltInAgents.IsBuiltInType(definition.Type))
                        {
                            logger?.LogWarning(
                                "Plugin '{Plugin}': agent type '{Type}' collides with a built-in agent type " +
                                "and will be ignored. Built-in agent types cannot be overridden by plugins.",
                                plugin.Name, definition.Type);
                        }
                        else
                        {
                            agents.Add(definition);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogError(
                        "Plugin '{Plugin}': unexpected error loading agents: {Message}",
                        plugin.Name, ex.Message);
                }
            }
            else
            {
                logger?.LogInformation(
                    "Plugin '{Plugin}': subagents not loaded — class not approved.", plugin.Name);
            }

            // Hooks
            if (trustFilter is null || trustFilter.IsClassAllowed(PluginComponentClass.Hook))
            {
                try
                {
                    var pluginHooks = PluginHookLoader.Load(plugin, workingDirectory, userCodaDir, logger);
                    hooks.AddRange(pluginHooks);
                }
                catch (Exception ex)
                {
                    logger?.LogError(
                        "Plugin '{Plugin}': unexpected error loading hooks: {Message}",
                        plugin.Name, ex.Message);
                }
            }
            else
            {
                logger?.LogInformation(
                    "Plugin '{Plugin}': hooks not loaded — class not approved.", plugin.Name);
            }

            // Commands (slash commands backed by Markdown prompt bodies)
            if (trustFilter is null || trustFilter.IsClassAllowed(PluginComponentClass.SlashCommand))
            {
                try
                {
                    commands.AddRange(PluginCommandLoader.Load(plugin, logger));
                }
                catch (Exception ex)
                {
                    logger?.LogError(
                        "Plugin '{Plugin}': unexpected error loading commands: {Message}",
                        plugin.Name, ex.Message);
                }
            }
            else
            {
                logger?.LogInformation(
                    "Plugin '{Plugin}': commands not loaded — class not approved.", plugin.Name);
            }
        }

        // MCP servers — filter per-plugin by class approval before the merge pass.
        var eligibleForMcp = trustStore is null
            ? plugins
            : plugins.Where(p =>
            {
                if (!p.IsEnabled) return false;
                var filter = BuildTrustFilter(p, workingDirectory, trustStore, logger);
                return !filter.BlocksAll && filter.IsClassAllowed(PluginComponentClass.McpServer);
            }).ToList();

        // MCP servers (merged across all eligible plugins to handle shadowing).
        IReadOnlyDictionary<string, (McpServerConfig Config, string PluginName)> mcpServers;
        try
        {
            mcpServers = PluginMcpLoader.Load(eligibleForMcp, logger);
        }
        catch (Exception ex)
        {
            logger?.LogError("Unexpected error loading plugin MCP servers: {Message}", ex.Message);
            mcpServers = new Dictionary<string, (McpServerConfig, string)>(StringComparer.Ordinal);
        }

        // Output styles and themes: apply workspace-trust filtering so project plugins in an
        // untrusted workspace cannot inject content into the session even for non-executable
        // components. LoadOutputStyles/LoadThemes are called outside the per-plugin loop (they
        // need the full collection), so we pre-filter here to a trust-safe list.
        var eligibleForStyles = trustStore is null
            ? (IReadOnlyList<PluginInfo>)plugins
            : plugins.Where(p =>
            {
                if (!p.IsEnabled) return false;
                var f = BuildTrustFilter(p, workingDirectory, trustStore, logger);
                return !f.BlocksAll;
            }).ToList();

        // Output styles: registered into the static registry (TUI/CLI path) and returned in
        // the composition for session-scoped serve resolution (I1 fix).
        var outputStyles = new List<OutputStyle>();
        try
        {
            outputStyles.AddRange(LoadOutputStyles(eligibleForStyles, logger));
        }
        catch (Exception ex)
        {
            logger?.LogError("Unexpected error registering plugin output styles: {Message}", ex.Message);
        }

        // Themes: registered into the static registry and returned for discoverability (M2 fix).
        var themes = new List<CodaTheme>();
        try
        {
            themes.AddRange(LoadThemes(eligibleForStyles, logger));
        }
        catch (Exception ex)
        {
            logger?.LogError("Unexpected error registering plugin themes: {Message}", ex.Message);
        }

        return new PluginComposition
        {
            Agents = agents,
            Hooks = hooks,
            McpServers = mcpServers,
            OutputStyles = outputStyles,
            Themes = themes,
            Commands = commands,
        };
    }

    /// <summary>
    /// Loads output styles from all enabled plugins, registers them into the static registry,
    /// and returns the list for session-scoped consumers.
    /// </summary>
    private static IReadOnlyList<OutputStyle> LoadOutputStyles(
        IReadOnlyList<PluginInfo> plugins,
        ILogger? logger)
    {
        var result = new List<OutputStyle>();
        foreach (var plugin in plugins)
        {
            if (!plugin.IsEnabled) continue;

            var dir = ResolveSubDirectory(plugin, plugin.Manifest?.OutputStyles ?? "output-styles");
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                var style = TryLoadOutputStyle(file, plugin.Name, logger);
                if (style is not null)
                {
                    BuiltInOutputStyles.RegisterPlugin(style, logger);
                    result.Add(style);
                }
            }
        }

        return result;
    }

    private static OutputStyle? TryLoadOutputStyle(string file, string pluginName, ILogger? logger)
    {
        try
        {
            var json = File.ReadAllText(file);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) return null;

            var description = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            var suffix = doc.RootElement.TryGetProperty("systemPromptSuffix", out var s) ? s.GetString() ?? string.Empty : string.Empty;

            return new OutputStyle(name, description, suffix);
        }
        catch (Exception ex)
        {
            logger?.LogError("Plugin '{Plugin}': failed to load output style '{File}': {Message}", pluginName, file, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Loads themes from all enabled plugins into a registry owned by this composition, then
    /// publishes that registry as the one the process resolves against. Composing again therefore
    /// replaces the previous composition's themes instead of accumulating on top of them.
    /// </summary>
    private static IReadOnlyList<CodaTheme> LoadThemes(
        IReadOnlyList<PluginInfo> plugins,
        ILogger? logger)
    {
        var registry = new PluginThemeRegistry();

        // Delegate to the existing loader which handles the complex JSON parsing.
        PluginThemeLoader.RegisterAll(plugins, logger, registry);
        CodaThemes.UsePluginRegistry(registry);

        return registry.All;
    }

    private static string ResolveSubDirectory(PluginInfo plugin, string relativePath) =>
        Path.GetFullPath(Path.Combine(plugin.Directory, relativePath));

    // -------------------------------------------------------------------------
    // Trust helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Determines whether <paramref name="plugin"/> is installed inside the current workspace
    /// directory (project-scoped) or outside it (user-scoped).  Any plugin whose canonical
    /// directory path begins with the workspace root is treated as project-scoped — this
    /// includes <c>.coda/plugins/</c> subdirectories and foreign manifests such as
    /// <c>.claude-plugin/</c> that arrive with a <c>git clone</c>.
    /// </summary>
    public static bool IsProjectPlugin(PluginInfo plugin, string workingDirectory) =>
        IsProjectScoped(plugin, workingDirectory);

    private static bool IsProjectScoped(PluginInfo plugin, string workingDirectory)
    {
        var workspacePath = Path.GetFullPath(workingDirectory) + Path.DirectorySeparatorChar;
        var pluginDir = Path.GetFullPath(plugin.Directory) + Path.DirectorySeparatorChar;
        return pluginDir.StartsWith(workspacePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a <see cref="PluginTrustFilter"/> for <paramref name="plugin"/> consulting
    /// <paramref name="trustStore"/>. Returns a filter that blocks all components when the
    /// plugin is project-scoped and the workspace is not trusted.
    /// </summary>
    private static PluginTrustFilter BuildTrustFilter(
        PluginInfo plugin,
        string workingDirectory,
        PluginTrustStore trustStore,
        ILogger? logger)
    {
        if (IsProjectScoped(plugin, workingDirectory))
        {
            if (!trustStore.IsWorkspaceTrusted(workingDirectory))
            {
                return PluginTrustFilter.BlockAll;
            }
        }

        // User-installed or workspace-trusted project plugin: check per-class approvals.
        var hash = PluginContentHash.Compute(plugin);

        if (!trustStore.HasApprovalRecord(hash))
        {
            // No approval record. User plugins installed before Phase 7 are treated as
            // all-approved for backward compatibility (the user explicitly installed them).
            // Project-scoped plugins within a trusted workspace are treated the same way:
            // they passed the workspace trust gate, so absence of a class record = all-approved.
            return PluginTrustFilter.AllApproved;
        }

        var approvedClasses = trustStore.GetApprovedClasses(hash);
        return new PluginTrustFilter(approvedClasses);
    }
}

/// <summary>
/// Captures the per-class approval state for a single plugin during composition.
/// </summary>
internal sealed class PluginTrustFilter
{
    /// <summary>A filter that blocks all components (workspace trust missing).</summary>
    public static readonly PluginTrustFilter BlockAll = new(null);

    /// <summary>A filter that allows all components (no approval record = backward compat).</summary>
    public static readonly PluginTrustFilter AllApproved = new(
        new HashSet<PluginComponentClass>(
            Enum.GetValues<PluginComponentClass>()));

    private readonly IReadOnlySet<PluginComponentClass>? _approved;

    public PluginTrustFilter(IReadOnlySet<PluginComponentClass>? approved)
    {
        this._approved = approved;
    }

    /// <summary><see langword="true"/> when the entire plugin is blocked (workspace untrusted).</summary>
    public bool BlocksAll => this._approved is null;

    /// <summary>Returns <see langword="true"/> when <paramref name="cls"/> was approved.</summary>
    public bool IsClassAllowed(PluginComponentClass cls) =>
        this._approved is not null && this._approved.Contains(cls);
}
