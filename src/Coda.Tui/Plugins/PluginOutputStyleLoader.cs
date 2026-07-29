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

            var dir = ResolveDirectory(plugin);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                var style = TryLoad(file, plugin.Name, logger);
                if (style is not null)
                {
                    BuiltInOutputStyles.RegisterPlugin(style, logger);
                }
            }
        }
    }

    private static OutputStyle? TryLoad(string file, string pluginName, ILogger? logger)
    {
        string json;
        try
        {
            json = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogError(
                "Plugin '{Plugin}': failed to read output style file '{File}': {Message}",
                pluginName, file, ex.Message);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                logger?.LogWarning(
                    "Plugin '{Plugin}': output style file '{File}' must be a JSON object — skipped.",
                    pluginName, file);
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
        catch (JsonException ex)
        {
            logger?.LogError(
                "Plugin '{Plugin}': output style file '{File}' contains invalid JSON: {Message}",
                pluginName, file, ex.Message);
            return null;
        }
    }

    private static string ResolveDirectory(PluginInfo plugin)
    {
        var relativePath = plugin.Manifest?.OutputStyles ?? "output-styles";
        return Path.GetFullPath(Path.Combine(plugin.Directory, relativePath));
    }
}
