using System.Text.Json;
using Coda.Agent.OutputStyles;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Plugins;

/// <summary>
/// Loads output style definitions contributed by plugins from the directory declared in the
/// manifest's <c>outputStyles</c> field (default: <c>output-styles/</c>). Each <c>.json</c> file
/// in that directory is parsed as an <see cref="OutputStyle"/>. Built-in names are protected:
/// a collision logs a warning and the plugin style is dropped.
/// </summary>
public static class PluginOutputStyleLoader
{
    /// <summary>
    /// Loads and registers all output styles contributed by a collection of plugins.
    /// Disabled plugins contribute nothing.
    /// </summary>
    public static void RegisterAll(IReadOnlyList<PluginInfo> plugins, ILogger? logger = null)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.IsEnabled) continue;

            var dir = PluginResourceLoader.ResolvePath(plugin, plugin.Manifest?.OutputStyles ?? "output-styles");
            var styles = PluginResourceLoader.LoadDirectory<OutputStyle>(
                dir, "*.json", plugin.Name, "output style", file => TryLoad(file, plugin.Name, logger), logger);

            foreach (var style in styles)
            {
                BuiltInOutputStyles.RegisterPlugin(style, logger);
            }
        }
    }

    private static OutputStyle? TryLoad(string file, string pluginName, ILogger? logger)
    {
        using var doc = PluginResourceLoader.TryReadJsonObject(file, pluginName, "output style", logger);
        if (doc is null)
        {
            return null;
        }

        var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            logger?.LogWarning(
                "Plugin '{Plugin}': output style file '{File}' has no 'name' field — skipped.",
                pluginName, file);
            return null;
        }

        var description = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : string.Empty;
        var suffix = doc.RootElement.TryGetProperty("systemPromptSuffix", out var s) ? s.GetString() : string.Empty;

        return new OutputStyle(
            Name: name,
            Description: description ?? string.Empty,
            SystemPromptSuffix: suffix ?? string.Empty);
    }
}
