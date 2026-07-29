using Coda.Tui.Skills;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Plugins;

/// <summary>
/// Loads plugin-contributed slash commands from the directory declared in the manifest's
/// <c>commands</c> field (default: <c>commands/</c>). Each <c>.md</c> file is parsed as a
/// <see cref="SkillDefinition"/> using the same YAML-subset frontmatter parser as user skills.
/// One malformed file does not prevent the rest from loading.
/// <para>
/// The resolved commands directory must reside inside the plugin directory. A traversal-style
/// path in the manifest (e.g. <c>../../evil</c>) is rejected with a logged error and an empty
/// result — regardless of what the manifest parser accepted.
/// </para>
/// </summary>
public static class PluginCommandLoader
{
    /// <summary>
    /// Loads all commands contributed by a single plugin. Returns an empty list when the plugin
    /// is disabled, has no manifest, the commands directory is absent, or the declared path
    /// escapes the plugin root.
    /// </summary>
    /// <param name="plugin">The plugin to load commands from.</param>
    /// <param name="logger">Optional diagnostic logger.</param>
    public static IReadOnlyList<SkillDefinition> Load(PluginInfo plugin, ILogger? logger = null)
    {
        if (!plugin.IsEnabled) return [];

        var relativePath = plugin.Manifest?.Commands ?? "commands";
        var commandsDir = PluginResourceLoader.ResolvePath(plugin, relativePath);

        // Containment guard: the commands directory must sit inside the plugin directory.
        if (!PluginResourceLoader.IsContained(commandsDir, plugin.Directory))
        {
            logger?.LogError(
                "Plugin '{Plugin}': commands path '{Path}' escapes the plugin directory — skipped.",
                plugin.Name, relativePath);
            return [];
        }

        return LoadFromDirectory(commandsDir, plugin.Name, logger);
    }

    /// <summary>
    /// Scans <paramref name="commandsDir"/> for <c>.md</c> files and parses each as a
    /// <see cref="SkillDefinition"/>. Malformed files are skipped with a logged error.
    /// The file stem (name without extension) is used as the fallback command name when the
    /// frontmatter has no <c>name</c> field.
    /// </summary>
    internal static IReadOnlyList<SkillDefinition> LoadFromDirectory(
        string commandsDir,
        string pluginName,
        ILogger? logger = null) =>
        PluginResourceLoader.LoadDirectory<SkillDefinition>(
            commandsDir,
            "*.md",
            pluginName,
            "command",
            file => SkillLoader.ParseSkillFile(
                File.ReadAllText(file),
                Path.GetFileNameWithoutExtension(file),
                file,
                SkillOrigin.Plugin),
            logger);
}
