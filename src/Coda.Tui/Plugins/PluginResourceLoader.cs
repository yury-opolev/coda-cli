using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Plugins;

/// <summary>
/// Shared plumbing for the plugin component loaders: resolving a manifest-declared path inside the
/// plugin directory, containment checking, and the read → parse → log-and-skip loop each loader
/// runs over its own file type.
/// </summary>
/// <remarks>
/// Every loader used to carry its own copy of these three steps, which is exactly the shape in
/// which a containment check drifts between components. They are defined once here instead.
/// </remarks>
internal static class PluginResourceLoader
{
    /// <summary>
    /// Resolves a manifest-declared relative path against the plugin directory.
    /// </summary>
    /// <param name="plugin">The contributing plugin.</param>
    /// <param name="relativePath">The manifest value, or the component type's default.</param>
    public static string ResolvePath(PluginInfo plugin, string relativePath) =>
        Path.GetFullPath(Path.Combine(plugin.Directory, relativePath));

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="resolvedPath"/> sits inside
    /// <paramref name="pluginDirectory"/>.
    /// </summary>
    /// <param name="resolvedPath">An already-resolved absolute path.</param>
    /// <param name="pluginDirectory">The plugin root.</param>
    /// <remarks>
    /// Both sides get a trailing separator before the prefix comparison, so a plugin directory of
    /// <c>C:\a</c> does not contain the sibling <c>C:\a-evil</c>.
    /// </remarks>
    public static bool IsContained(string resolvedPath, string pluginDirectory)
    {
        var normalizedDir = Normalize(pluginDirectory);
        var normalizedPath = Normalize(resolvedPath);
        return normalizedPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase);

        static string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Reads a plugin file, logging and returning <see langword="null"/> on an I/O or permission
    /// failure rather than aborting the surrounding load.
    /// </summary>
    /// <param name="file">Absolute path to the file.</param>
    /// <param name="pluginName">Contributing plugin name, for the log message.</param>
    /// <param name="resourceKind">Human-readable component name, e.g. <c>"theme"</c>.</param>
    /// <param name="logger">Optional diagnostic logger.</param>
    public static string? TryReadText(string file, string pluginName, string resourceKind, ILogger? logger)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogError(
                "Plugin '{Plugin}': failed to read {Kind} file '{File}': {Message}",
                pluginName, resourceKind, file, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reads and parses a plugin JSON file whose root must be an object. Returns
    /// <see langword="null"/> (after logging) when the file cannot be read, is not valid JSON, or
    /// does not have an object root. The caller owns the returned document.
    /// </summary>
    /// <param name="file">Absolute path to the file.</param>
    /// <param name="pluginName">Contributing plugin name, for the log message.</param>
    /// <param name="resourceKind">Human-readable component name, e.g. <c>"output style"</c>.</param>
    /// <param name="logger">Optional diagnostic logger.</param>
    public static JsonDocument? TryReadJsonObject(
        string file,
        string pluginName,
        string resourceKind,
        ILogger? logger)
    {
        if (TryReadText(file, pluginName, resourceKind, logger) is not { } json)
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            logger?.LogError(
                "Plugin '{Plugin}': {Kind} file '{File}' contains invalid JSON: {Message}",
                pluginName, resourceKind, file, ex.Message);
            return null;
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            logger?.LogWarning(
                "Plugin '{Plugin}': {Kind} file '{File}' must be a JSON object — skipped.",
                pluginName, resourceKind, file);
            doc.Dispose();
            return null;
        }

        return doc;
    }

    /// <summary>
    /// Runs <paramref name="parse"/> over every file matching <paramref name="searchPattern"/> in
    /// <paramref name="directory"/>, collecting the non-null results. A missing directory yields an
    /// empty list; a file whose parse throws is logged and skipped so one bad file never blocks the
    /// rest.
    /// </summary>
    /// <typeparam name="T">The component type produced by <paramref name="parse"/>.</typeparam>
    /// <param name="directory">Directory to scan; may not exist.</param>
    /// <param name="searchPattern">File glob, e.g. <c>"*.json"</c>.</param>
    /// <param name="pluginName">Contributing plugin name, for the log message.</param>
    /// <param name="resourceKind">Human-readable component name, e.g. <c>"command"</c>.</param>
    /// <param name="parse">Parses one file; returns <see langword="null"/> to skip it.</param>
    /// <param name="logger">Optional diagnostic logger.</param>
    public static List<T> LoadDirectory<T>(
        string directory,
        string searchPattern,
        string pluginName,
        string resourceKind,
        Func<string, T?> parse,
        ILogger? logger)
        where T : class
    {
        var result = new List<T>();
        if (!Directory.Exists(directory))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (parse(file) is { } parsed)
                {
                    result.Add(parsed);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogError(
                    "Plugin '{Plugin}': failed to read {Kind} file '{File}': {Message}",
                    pluginName, resourceKind, file, ex.Message);
            }
            catch (Exception ex)
            {
                logger?.LogError(
                    "Plugin '{Plugin}': failed to parse {Kind} file '{File}': {Message}",
                    pluginName, resourceKind, file, ex.Message);
            }
        }

        return result;
    }
}
