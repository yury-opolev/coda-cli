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
        var commandsDir = Path.GetFullPath(Path.Combine(plugin.Directory, relativePath));

        // Containment guard: the commands directory must sit inside the plugin directory.
        if (!IsContained(commandsDir, plugin.Directory))
        {
            logger?.LogError(
                "Plugin '{Plugin}': commands path '{Path}' escapes the plugin directory — skipped.",
                plugin.Name, relativePath);
            return [];
        }

        if (!Directory.Exists(commandsDir)) return [];

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
        ILogger? logger = null)
    {
        var result = new List<SkillDefinition>();

        foreach (var file in Directory.EnumerateFiles(commandsDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var content = File.ReadAllText(file);
                var fallbackName = Path.GetFileNameWithoutExtension(file);
                var definition = SkillLoader.ParseSkillFile(content, fallbackName, file, SkillOrigin.Plugin);
                result.Add(definition);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogError(
                    "Plugin '{Plugin}': failed to read command file '{File}': {Message}",
                    pluginName, file, ex.Message);
            }
            catch (Exception ex)
            {
                logger?.LogError(
                    "Plugin '{Plugin}': failed to parse command file '{File}': {Message}",
                    pluginName, file, ex.Message);
            }
        }

        return result;
    }

    private static bool IsContained(string resolvedPath, string pluginDirectory)
    {
        var normalizedDir = Path.GetFullPath(pluginDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        // Append separator to the candidate path so a plugin dir of "C:\a" does not match
        // the sibling "C:\a-evil" (which would also start with "C:\a").
        var normalizedPath = Path.GetFullPath(resolvedPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase);
    }
}
