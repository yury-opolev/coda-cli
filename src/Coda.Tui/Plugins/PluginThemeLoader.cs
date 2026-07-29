using System.Text.Json;
using Coda.Tui.Ui.Rendering;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Plugins;

/// <summary>
/// Loads theme definitions contributed by plugins from the directory declared in the manifest's
/// <c>themes</c> field (default: <c>themes/</c>). Each <c>.json</c> file in that directory is
/// parsed as a <see cref="CodaTheme"/> using the built-in Default TUI theme with a custom
/// <see cref="ConsolePalette"/>. Built-in theme names are protected: a collision logs a warning
/// and the plugin theme is dropped.
/// </summary>
internal static class PluginThemeLoader
{
    /// <summary>
    /// Loads and registers all themes contributed by a collection of plugins.
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
                var theme = TryLoad(file, plugin.Name, logger);
                if (theme is not null)
                {
                    CodaThemes.RegisterPlugin(theme, logger);
                }
            }
        }
    }

    private static CodaTheme? TryLoad(string file, string pluginName, ILogger? logger)
    {
        string json;
        try
        {
            json = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogError(
                "Plugin '{Plugin}': failed to read theme file '{File}': {Message}",
                pluginName, file, ex.Message);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                logger?.LogWarning(
                    "Plugin '{Plugin}': theme file '{File}' must be a JSON object — skipped.",
                    pluginName, file);
                return null;
            }

            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                logger?.LogWarning(
                    "Plugin '{Plugin}': theme file '{File}' has no 'name' field — skipped.",
                    pluginName, file);
                return null;
            }

            var displayName = doc.RootElement.TryGetProperty("displayName", out var dn)
                ? dn.GetString() ?? name
                : name;

            if (!doc.RootElement.TryGetProperty("consolePalette", out var cp) ||
                cp.ValueKind != JsonValueKind.Object)
            {
                logger?.LogWarning(
                    "Plugin '{Plugin}': theme file '{File}' has no 'consolePalette' object — skipped.",
                    pluginName, file);
                return null;
            }

            var accent = cp.TryGetProperty("accent", out var a) ? a.GetString() : null;
            var dim = cp.TryGetProperty("dim", out var di) ? di.GetString() : null;
            var success = cp.TryGetProperty("success", out var s) ? s.GetString() : null;
            var warn = cp.TryGetProperty("warn", out var w) ? w.GetString() : null;
            var error = cp.TryGetProperty("error", out var e) ? e.GetString() : null;

            if (accent is null || dim is null || success is null || warn is null || error is null)
            {
                logger?.LogWarning(
                    "Plugin '{Plugin}': theme file '{File}' consolePalette is missing required color fields " +
                    "(accent, dim, success, warn, error) — skipped.",
                    pluginName, file);
                return null;
            }

            var palette = new ConsolePalette(accent, dim, success, warn, error);

            // Plugin themes use the Default TUI theme for full terminal UI rendering,
            // supplying only the console color palette for non-TUI output paths.
            return new CodaTheme(
                Name: name,
                DisplayName: displayName,
                Tui: CodaThemes.Default.Tui,
                Console: palette);
        }
        catch (JsonException ex)
        {
            logger?.LogError(
                "Plugin '{Plugin}': theme file '{File}' contains invalid JSON: {Message}",
                pluginName, file, ex.Message);
            return null;
        }
    }

    private static string ResolveDirectory(PluginInfo plugin)
    {
        var relativePath = plugin.Manifest?.Themes ?? "themes";
        return Path.GetFullPath(Path.Combine(plugin.Directory, relativePath));
    }
}
