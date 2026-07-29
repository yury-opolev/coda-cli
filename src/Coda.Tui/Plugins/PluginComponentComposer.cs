using Coda.Agent.Hooks;
using Coda.Agent.OutputStyles;
using Coda.Agent.Subagents;
using Coda.Mcp;
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
    /// Themes contributed by enabled plugins. Registered into the static
    /// <see cref="CodaThemes"/> registry as a side-effect of
    /// <see cref="PluginComponentComposer.Compose"/>; this list is available for any future
    /// session-scoped theme resolution path.
    /// </summary>
    internal IReadOnlyList<CodaTheme> Themes { get; init; } = [];
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
    public static PluginComposition Compose(
        IReadOnlyList<PluginInfo> plugins,
        string workingDirectory,
        string? userCodaDir = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var agents = new List<SubagentDefinition>();
        var hooks = new List<UserHook>();

        foreach (var plugin in plugins)
        {
            if (!plugin.IsEnabled) continue;

            // Agents (M1: reject definitions that shadow a built-in type)
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

            // Hooks
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

        // MCP servers (merged across all plugins at once to handle shadowing).
        IReadOnlyDictionary<string, (McpServerConfig Config, string PluginName)> mcpServers;
        try
        {
            mcpServers = PluginMcpLoader.Load(plugins, logger);
        }
        catch (Exception ex)
        {
            logger?.LogError("Unexpected error loading plugin MCP servers: {Message}", ex.Message);
            mcpServers = new Dictionary<string, (McpServerConfig, string)>(StringComparer.Ordinal);
        }

        // Output styles: registered into the static registry (TUI/CLI path) and returned in
        // the composition for session-scoped serve resolution (I1 fix).
        var outputStyles = new List<OutputStyle>();
        try
        {
            outputStyles.AddRange(LoadOutputStyles(plugins, logger));
        }
        catch (Exception ex)
        {
            logger?.LogError("Unexpected error registering plugin output styles: {Message}", ex.Message);
        }

        // Themes: registered into the static registry and returned for discoverability (M2 fix).
        var themes = new List<CodaTheme>();
        try
        {
            themes.AddRange(LoadThemes(plugins, logger));
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
    /// Loads themes from all enabled plugins, registers them into the static registry,
    /// and returns the list for discoverability via <see cref="CodaThemes.GetPluginThemes"/>.
    /// </summary>
    private static IReadOnlyList<CodaTheme> LoadThemes(
        IReadOnlyList<PluginInfo> plugins,
        ILogger? logger)
    {
        // Delegate to the existing loader which handles the complex JSON parsing.
        PluginThemeLoader.RegisterAll(plugins, logger);

        // Return a snapshot of newly registered themes for the composition result.
        return [.. CodaThemes.GetPluginThemes()];
    }

    private static string ResolveSubDirectory(PluginInfo plugin, string relativePath) =>
        Path.GetFullPath(Path.Combine(plugin.Directory, relativePath));
}
