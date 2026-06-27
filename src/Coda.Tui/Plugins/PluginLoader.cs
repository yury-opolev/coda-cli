using System.Text.Json;

namespace Coda.Tui.Plugins;

/// <summary>Discovers plugin directories under <c>.coda/plugins/*/</c> (project and user).</summary>
public static class PluginLoader
{
    private const string PluginFileName = "plugin.json";
    private static readonly string RelativePluginsPath = Path.Combine(".coda", "plugins");

    /// <summary>
    /// Loads plugins from user-level (~/.coda/plugins) and project-level (.coda/plugins in
    /// <paramref name="workingDirectory"/>). Project plugins override user plugins with the same name.
    /// Missing directories are tolerated. Malformed plugin.json is skipped or defaulted gracefully.
    /// </summary>
    public static IReadOnlyList<PluginInfo> Load(string workingDirectory, string? userCodaDir = null)
    {
        var userBase = userCodaDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".coda");

        var userPluginsPath = Path.Combine(userBase, "plugins");
        var projectPluginsPath = Path.Combine(workingDirectory, RelativePluginsPath);

        // User plugins first, then project plugins override by name.
        var byName = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in LoadFromDirectory(userPluginsPath))
        {
            byName[plugin.Name] = plugin;
        }

        foreach (var plugin in LoadFromDirectory(projectPluginsPath))
        {
            byName[plugin.Name] = plugin;
        }

        return [.. byName.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Returns the <c>skills</c> subdirectories of all discovered plugins that actually exist,
    /// so that <see cref="Coda.Tui.Skills.SkillLoader"/> can include plugin-bundled skills.
    /// </summary>
    public static IReadOnlyList<string> SkillDirsFor(string workingDirectory, string? userCodaDir = null)
    {
        var plugins = Load(workingDirectory, userCodaDir);
        var result = new List<string>(plugins.Count);

        foreach (var plugin in plugins)
        {
            var skillsDir = Path.Combine(plugin.Directory, "skills");
            if (Directory.Exists(skillsDir))
            {
                result.Add(skillsDir);
            }
        }

        return result;
    }

    private static IEnumerable<PluginInfo> LoadFromDirectory(string pluginsRoot)
    {
        if (!Directory.Exists(pluginsRoot))
        {
            yield break;
        }

        foreach (var subDir in Directory.EnumerateDirectories(pluginsRoot))
        {
            var pluginFile = Path.Combine(subDir, PluginFileName);
            var dirName = Path.GetFileName(subDir);

            if (!File.Exists(pluginFile))
            {
                // No plugin.json → skip this directory entirely.
                continue;
            }

            PluginInfo? plugin = null;
            try
            {
                var json = File.ReadAllText(pluginFile);
                plugin = ParsePluginJson(json, dirName, subDir);
            }
            catch
            {
                // Malformed/unreadable plugin.json → use defaults.
                plugin = new PluginInfo(dirName, "0.0.0", string.Empty, subDir);
            }

            if (plugin is not null)
            {
                yield return plugin;
            }
        }
    }

    internal static PluginInfo ParsePluginJson(string json, string directoryName, string directory)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = TryGetString(root, "name") ?? directoryName;
        var version = TryGetString(root, "version") ?? "0.0.0";
        var description = TryGetString(root, "description") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            name = directoryName;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            version = "0.0.0";
        }

        return new PluginInfo(name, version, description, directory);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }
}
