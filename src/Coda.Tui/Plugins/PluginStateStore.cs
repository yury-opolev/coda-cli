using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Common;

namespace Coda.Tui.Plugins;

/// <summary>
/// Persists per-plugin state — enabled/disabled overrides, explicit-enable records, installed
/// version metadata, and non-secret user-config values — to
/// <c>&lt;codaDir&gt;/plugin-state.json</c>.
/// </summary>
/// <remarks>
/// Writes are atomic (temp-file + move), matching the pattern used by
/// <c>SettingsWriter</c> throughout the codebase.
/// </remarks>
public sealed class PluginStateStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string filePath;
    private readonly string codaDir;

    /// <summary>
    /// Creates a store that reads and writes <c>plugin-state.json</c> inside
    /// <paramref name="codaDir"/>.
    /// </summary>
    public PluginStateStore(string codaDir)
    {
        this.codaDir = codaDir;
        this.filePath = Path.Combine(codaDir, "plugin-state.json");
    }

    // -------------------------------------------------------------------------
    // Enable / disable
    // -------------------------------------------------------------------------

    /// <summary>
    /// Persists an enable or disable override for <paramref name="pluginName"/>.
    /// <para>
    /// Disabling adds the name to <c>disabledPlugins</c> and removes it from
    /// <c>explicitlyEnabled</c>. Enabling does the reverse.
    /// </para>
    /// </summary>
    public void SetEnabled(string pluginName, bool enabled)
    {
        var doc = this.Load();

        if (enabled)
        {
            doc.DisabledPlugins.Remove(pluginName);
            doc.ExplicitlyEnabled.Add(pluginName);
        }
        else
        {
            doc.DisabledPlugins.Add(pluginName);
            doc.ExplicitlyEnabled.Remove(pluginName);
        }

        this.Save(doc);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the plugin should be loaded.
    /// </summary>
    /// <param name="pluginName">Plugin name to check.</param>
    /// <param name="defaultEnabled">
    /// The plugin manifest's <c>defaultEnabled</c> flag. When <see langword="false"/> the plugin
    /// starts disabled unless the user has explicitly enabled it.
    /// </param>
    public bool IsEnabled(string pluginName, bool defaultEnabled = true)
    {
        var doc = this.Load();

        if (doc.DisabledPlugins.Contains(pluginName))
        {
            return false;
        }

        if (!defaultEnabled && !doc.ExplicitlyEnabled.Contains(pluginName))
        {
            return false;
        }

        return true;
    }

    // -------------------------------------------------------------------------
    // Install metadata
    // -------------------------------------------------------------------------

    /// <summary>Records or updates the install information for <paramref name="pluginName"/>.</summary>
    public void SetInstalledInfo(string pluginName, PluginInstallInfo info)
    {
        var doc = this.Load();
        doc.InstalledVersions[pluginName] = info;
        this.Save(doc);
    }

    /// <summary>
    /// Returns the recorded install information for <paramref name="pluginName"/>, or
    /// <see langword="null"/> when no record exists.
    /// </summary>
    public PluginInstallInfo? GetInstalledInfo(string pluginName)
    {
        var doc = this.Load();
        return doc.InstalledVersions.TryGetValue(pluginName, out var info) ? info : null;
    }

    // -------------------------------------------------------------------------
    // Non-secret user-config values
    // -------------------------------------------------------------------------

    /// <summary>
    /// Persists non-secret user-config values for <paramref name="pluginName"/>.
    /// Existing keys are merged; keys not in <paramref name="values"/> are preserved.
    /// </summary>
    public void SetPluginConfig(string pluginName, IReadOnlyDictionary<string, string> values)
    {
        var doc = this.Load();

        if (!doc.PluginConfig.TryGetValue(pluginName, out var existing))
        {
            existing = new Dictionary<string, string>(StringComparer.Ordinal);
            doc.PluginConfig[pluginName] = existing;
        }

        foreach (var (k, v) in values)
        {
            existing[k] = v;
        }

        this.Save(doc);
    }

    /// <summary>
    /// Returns the non-secret user-config values for <paramref name="pluginName"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetPluginConfig(string pluginName)
    {
        var doc = this.Load();
        return doc.PluginConfig.TryGetValue(pluginName, out var cfg)
            ? cfg
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Load / save
    // -------------------------------------------------------------------------

    private StateDocument Load()
    {
        if (!File.Exists(this.filePath))
        {
            return new StateDocument();
        }

        try
        {
            var json = File.ReadAllText(this.filePath);
            var root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();

            var doc = new StateDocument();

            if (root["disabledPlugins"] is JsonArray disabledArr)
            {
                foreach (var item in disabledArr)
                {
                    if (item is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        doc.DisabledPlugins.Add(s);
                    }
                }
            }

            if (root["explicitlyEnabled"] is JsonArray enabledArr)
            {
                foreach (var item in enabledArr)
                {
                    if (item is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        doc.ExplicitlyEnabled.Add(s);
                    }
                }
            }

            if (root["installedVersions"] is JsonObject versionsObj)
            {
                foreach (var (name, node) in versionsObj)
                {
                    if (node is not JsonObject infoObj)
                    {
                        continue;
                    }

                    var version = infoObj["version"]?.GetValue<string>() ?? "0.0.0";
                    var source = infoObj["source"]?.GetValue<string>() ?? "local";
                    var gitUrl = infoObj["gitUrl"]?.GetValue<string>();
                    var commit = infoObj["commit"]?.GetValue<string>();
                    var marketplace = infoObj["marketplace"]?.GetValue<string>();
                    DateTimeOffset.TryParse(infoObj["installedAt"]?.GetValue<string>(), out var installedAt);

                    doc.InstalledVersions[name] = new PluginInstallInfo(
                        version, source, gitUrl, commit, installedAt, marketplace);
                }
            }

            if (root["pluginConfig"] is JsonObject configObj)
            {
                foreach (var (pluginName, node) in configObj)
                {
                    if (node is not JsonObject cfgObj)
                    {
                        continue;
                    }

                    var cfg = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (k, v) in cfgObj)
                    {
                        if (v is JsonValue val && val.TryGetValue<string>(out var s))
                        {
                            cfg[k] = s;
                        }
                    }

                    doc.PluginConfig[pluginName] = cfg;
                }
            }

            return doc;
        }
        catch
        {
            return new StateDocument();
        }
    }

    private void Save(StateDocument doc)
    {
        Directory.CreateDirectory(this.codaDir);

        var root = new JsonObject
        {
            ["disabledPlugins"] = new JsonArray(
                doc.DisabledPlugins.Order().Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["explicitlyEnabled"] = new JsonArray(
                doc.ExplicitlyEnabled.Order().Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        };

        var versionsObj = new JsonObject();
        foreach (var (name, info) in doc.InstalledVersions)
        {
            var infoObj = new JsonObject
            {
                ["version"] = info.Version,
                ["source"] = info.Source,
                ["installedAt"] = info.InstalledAt.ToString("O"),
            };
            if (info.GitUrl is not null)
            {
                infoObj["gitUrl"] = info.GitUrl;
            }

            if (info.Commit is not null)
            {
                infoObj["commit"] = info.Commit;
            }

            if (info.Marketplace is not null)
            {
                infoObj["marketplace"] = info.Marketplace;
            }

            versionsObj[name] = infoObj;
        }

        root["installedVersions"] = versionsObj;

        var configObj = new JsonObject();
        foreach (var (pluginName, values) in doc.PluginConfig)
        {
            var valObj = new JsonObject();
            foreach (var (k, v) in values)
            {
                valObj[k] = v;
            }

            configObj[pluginName] = valObj;
        }

        root["pluginConfig"] = configObj;

        var json = root.ToJsonString(WriteOptions);
        AtomicFile.WriteAllText(this.filePath, json);
    }

    private sealed class StateDocument
    {
        public HashSet<string> DisabledPlugins { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ExplicitlyEnabled { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, PluginInstallInfo> InstalledVersions { get; } =
            new Dictionary<string, PluginInstallInfo>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Dictionary<string, string>> PluginConfig { get; } =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    }
}
