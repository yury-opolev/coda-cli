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
    /// <param name="workingDirectory">The project working directory.</param>
    /// <param name="userCodaDir">
    /// The user-level <c>.coda</c> directory. Defaults to <c>~/.coda</c> when <see langword="null"/>.
    /// </param>
    /// <param name="stateStore">
    /// Optional state store for enable/disable overrides. When <see langword="null"/> all plugins
    /// are returned regardless of their enable state.
    /// </param>
    /// <param name="clock">
    /// Clock used to decide which orphan directories have expired; defaults to
    /// <see cref="TimeProvider.System"/> when <see langword="null"/>.
    /// </param>
    public static IReadOnlyList<PluginInfo> Load(
        string workingDirectory,
        string? userCodaDir = null,
        PluginStateStore? stateStore = null,
        TimeProvider? clock = null)
    {
        var userBase = userCodaDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".coda");

        // Purge expired orphans on every load (14-day grace period).
        PluginOrphanManager.PurgeExpired(userBase, clock ?? TimeProvider.System);

        var userPluginsPath = Path.Combine(userBase, "plugins");
        var projectPluginsPath = Path.Combine(workingDirectory, RelativePluginsPath);

        // User plugins first, then project plugins override by name.
        var byName = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in LoadFromDirectory(userPluginsPath, stateStore))
        {
            byName[plugin.Name] = plugin;
        }

        foreach (var plugin in LoadFromDirectory(projectPluginsPath, stateStore))
        {
            byName[plugin.Name] = plugin;
        }

        // When a state store is present, return only enabled plugins so disabled ones
        // contribute nothing (no skills, no LSP servers, no hooks).
        if (stateStore is not null)
        {
            return [.. byName.Values
                .Where(p => p.IsEnabled)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
        }

        return [.. byName.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Returns the <c>skills</c> subdirectories of all discovered plugins that actually exist,
    /// so that <see cref="Coda.Tui.Skills.SkillLoader"/> can include plugin-bundled skills.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>skills</c> manifest field is <b>additive</b>: its entries are included alongside
    /// the conventional <c>skills/</c> subdirectory scan, following Claude Code's rule.
    /// </para>
    /// <para>
    /// Manifest paths that contain <c>${...}</c> variable references are interpolated here before
    /// the directory-existence check. An expanded path that resolves to an absolute location
    /// (e.g. <c>${CODA_PLUGIN_DATA}/skills</c>) is used as-is; the variable was safe because the
    /// parser already rejected any traversal in the unexpanded form.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> SkillDirsFor(
        string workingDirectory,
        string? userCodaDir = null,
        PluginStateStore? stateStore = null)
    {
        var userBase = userCodaDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".coda");

        var plugins = Load(workingDirectory, userCodaDir, stateStore);
        var result = new List<string>(plugins.Count * 2);

        foreach (var plugin in plugins)
        {
            // Convention directory — always included first.
            var conventionDir = Path.Combine(plugin.Directory, "skills");
            if (Directory.Exists(conventionDir))
            {
                result.Add(conventionDir);
            }

            // Extra paths declared in the manifest (additive).
            if (plugin.Manifest is not null)
            {
                // Compute the plugin-data directory for variable interpolation.
                var pluginDataDir = Path.Combine(userBase, "plugin-data", plugin.Name);

                foreach (var rawPath in plugin.Manifest.Skills)
                {
                    if (string.IsNullOrEmpty(rawPath))
                    {
                        continue;
                    }

                    // Interpolate ${CODA_PLUGIN_ROOT}, ${CODA_PLUGIN_DATA}, ${CODA_PROJECT_DIR}
                    var extraPath = PluginVariableInterpolator.Interpolate(
                        rawPath,
                        pluginRoot: plugin.Directory,
                        pluginDataDir: pluginDataDir,
                        projectDir: workingDirectory);

                    var fullExtra = Path.IsPathRooted(extraPath)
                        ? extraPath
                        : Path.Combine(plugin.Directory, extraPath);

                    fullExtra = Path.GetFullPath(fullExtra);

                    if (Directory.Exists(fullExtra)
                        && !string.Equals(fullExtra, conventionDir, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(fullExtra);
                    }
                }
            }
        }

        return result;
    }

    private static IEnumerable<PluginInfo> LoadFromDirectory(
        string pluginsRoot,
        PluginStateStore? stateStore)
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
            catch (PluginManifestPathException)
            {
                // Path containment violation — skip entirely; do NOT fall back to legacy.
                continue;
            }
            catch
            {
                // Malformed/unreadable plugin.json → use defaults.
                plugin = new PluginInfo(dirName, "0.0.0", string.Empty, subDir);
            }

            if (plugin is not null)
            {
                // Apply enable/disable state when a store is present.
                if (stateStore is not null)
                {
                    var defaultEnabled = plugin.Manifest?.DefaultEnabled ?? true;
                    var isEnabled = stateStore.IsEnabled(plugin.Name, defaultEnabled);
                    plugin = plugin with { IsEnabled = isEnabled };
                }

                yield return plugin;
            }
        }
    }

    /// <summary>
    /// Parses a <c>plugin.json</c> string into a <see cref="PluginInfo"/>.
    /// Tries the full Phase 3 manifest parser first; falls back to the legacy three-field
    /// behaviour (with directory-name fallback) only when the new parser rejects the
    /// <em>name</em> field — so existing plugins that carry only <c>name</c>/<c>version</c>/
    /// <c>description</c> continue to work exactly as before.
    /// Path-containment violations (<see cref="PluginManifestPathException"/>) are NOT caught
    /// here; they propagate so the caller can skip the plugin without silently dropping the
    /// violation.
    /// </summary>
    internal static PluginInfo ParsePluginJson(string json, string directoryName, string directory)
    {
        // Phase 3 parser — strict about name and paths, ignores unknown fields.
        try
        {
            var manifest = PluginManifestParser.Parse(json, directory);
            return new PluginInfo(manifest.Name, manifest.Version, manifest.Description, directory)
            {
                Manifest = manifest,
            };
        }
        catch (PluginManifestPathException)
        {
            // Path violations must NOT be swallowed — propagate to the caller.
            throw;
        }
        catch (PluginManifestParseException)
        {
            // Missing/empty/non-kebab-case name → fall through to legacy path.
        }

        // Legacy three-field path (backward compatibility — name/version/description only).
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
