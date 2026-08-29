using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent.Permissions;
using Microsoft.Extensions.Logging;
using Coda.Common;

namespace Coda.Agent.Settings;

/// <summary>
/// Writes user-level settings to <c>~/.coda/settings.json</c>, preserving any keys
/// the loader doesn't model (permissions, hooks, lspServers, …). Mirrors the
/// reference client's "update a single setting" behavior.
/// </summary>
public static class SettingsWriter
{
    /// <summary>
    /// Persist the GitHub Enterprise Cloud data-residency domain used by the GitHub
    /// Copilot provider (e.g. <c>octocorp.ghe.com</c>). <see langword="null"/> leaves it
    /// unchanged; an empty string removes it (reset to public github.com). Atomic (temp
    /// file + move), preserving all other keys.
    /// </summary>
    public static void SetGitHubEnterpriseDomain(string? domain, string? userSettingsDir = null)
    {
        var homeDir = userSettingsDir
            ?? Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(homeDir, ".coda");
        var file = Path.Combine(dir, "settings.json");

        JsonObject root;
        try
        {
            root = (File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null) ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        ApplyKey(root, "githubEnterpriseDomain", domain);

        Directory.CreateDirectory(dir);
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(file, json);
    }

    /// <summary>
    /// Set the persisted default provider. A <see langword="null"/> value leaves it unchanged;
    /// an empty string removes it. (There is no global default model — a model is only ever
    /// configured per provider via <see cref="SetUserModelForProvider"/>.)
    /// </summary>
    public static void SetUserDefaultProvider(string? defaultProvider, string? userSettingsDir = null)
    {
        var homeDir = userSettingsDir
            ?? Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(homeDir, ".coda");
        var file = Path.Combine(dir, "settings.json");

        JsonObject root;
        try
        {
            root = (File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null) ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject(); // corrupt file → start fresh rather than throw
        }

        ApplyKey(root, "defaultProvider", defaultProvider);

        Directory.CreateDirectory(dir);

        // Atomic write: serialize to a temp file in the same directory, then replace.
        // A crash or concurrent writer can't truncate settings.json (which also holds
        // the user's permissions/hooks/lspServers).
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(file, json);
    }

    /// <summary>
    /// Persist the model <b>for a specific provider</b> under the <c>modelByProvider</c> object
    /// (e.g. <c>github-copilot -&gt; claude-opus-4.8</c>), preserving all other providers' entries
    /// and all other settings keys. This is what the <c>/model</c> command writes so a model belongs
    /// to its provider — there is no provider-agnostic default model. Atomic (temp file + move).
    /// </summary>

    /// <summary>
    /// Set the persisted theme name. A <see langword="null"/> value leaves it unchanged;
    /// an empty string removes it. Atomic (temp file + move), preserving all other keys.
    /// </summary>
    public static void SetUserTheme(string? themeName, string? userSettingsDir = null)
    {
        var homeDir = userSettingsDir
            ?? Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(homeDir, ".coda");
        var file = Path.Combine(dir, "settings.json");

        JsonObject root;
        try
        {
            root = (File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null) ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        ApplyKey(root, "theme", themeName);

        Directory.CreateDirectory(dir);
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(file, json);
    }
    public static void SetUserModelForProvider(string providerId, string model, string? userSettingsDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var homeDir = userSettingsDir
            ?? Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(homeDir, ".coda");
        var file = Path.Combine(dir, "settings.json");

        JsonObject root;
        try
        {
            root = (File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null) ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        var byProvider = root["modelByProvider"] as JsonObject ?? new JsonObject();
        byProvider[providerId] = model;
        root["modelByProvider"] = byProvider;

        Directory.CreateDirectory(dir);
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(file, json);
    }

    /// <summary>
    /// Persists the telemetry block to user settings, preserving all other keys
    /// (including telemetry sub-keys this method does not manage). Writes the level
    /// as a lowercase word (e.g. "debug"). Atomic (temp file + move).
    /// </summary>
    public static void SetTelemetry(bool enabled, LogLevel level, bool stderr, string? userSettingsDir = null)
    {
        var homeDir = userSettingsDir
            ?? Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(homeDir, ".coda");
        var file = Path.Combine(dir, "settings.json");

        JsonObject root;
        try
        {
            root = (File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null) ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        var existing = root["telemetry"] as JsonObject ?? new JsonObject();
        existing["enabled"] = enabled;
        existing["level"] = level.ToString().ToLowerInvariant();
        existing["stderr"] = stderr;
        root["telemetry"] = existing;

        Directory.CreateDirectory(dir);
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(file, json);
    }

    /// <summary>
    /// Merge permission rules into the <c>permissions</c> section of the settings file at
    /// <paramref name="settingsFilePath"/>. Existing rules are preserved and duplicates are
    /// dropped. Atomic (temp file + move), preserving all other keys.
    /// </summary>
    /// <remarks>
    /// This is what a <c>PermissionRequest</c> hook's <c>updatedPermissions</c> writes for
    /// <c>scope:"project"</c> and <c>scope:"user"</c>. Failures are logged and swallowed — a
    /// settings file that cannot be written must never fail the turn. Mode changes (<c>setMode</c>)
    /// are session-scoped only and are never persisted to disk.
    /// </remarks>
    /// <param name="addAllow">Rules to append to <c>permissions.allow</c>.</param>
    /// <param name="addDeny">Rules to append to <c>permissions.deny</c>.</param>
    /// <param name="settingsFilePath">The full path of the <c>settings.json</c> file to update.</param>
    /// <param name="logger">Optional logger for write failures and skipped malformed rules.</param>
    public static void AddPermissionRules(
        IReadOnlyList<string> addAllow,
        IReadOnlyList<string> addDeny,
        string settingsFilePath,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);

        try
        {
            JsonObject root;
            try
            {
                root = (File.Exists(settingsFilePath)
                    ? JsonNode.Parse(File.ReadAllText(settingsFilePath)) as JsonObject
                    : null) ?? new JsonObject();
            }
            catch (JsonException)
            {
                root = new JsonObject(); // corrupt file → start fresh rather than throw
            }

            var permissions = root["permissions"] as JsonObject ?? new JsonObject();
            MergeRules(permissions, "allow", addAllow, logger);
            MergeRules(permissions, "deny", addDeny, logger);

            root["permissions"] = permissions;

            var dir = Path.GetDirectoryName(Path.GetFullPath(settingsFilePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(settingsFilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            logger?.LogWarning(ex, "Failed to persist permission rules to {SettingsFile}", settingsFilePath);
        }
    }

    /// <summary>Append rules to a string array under <paramref name="key"/>, skipping duplicates and malformed entries.</summary>
    private static void MergeRules(JsonObject permissions, string key, IReadOnlyList<string> additions, ILogger? logger)
    {
        if (additions is not { Count: > 0 })
        {
            return;
        }

        var array = permissions[key] as JsonArray ?? new JsonArray();
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in array)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                existing.Add(text);
            }
        }

        foreach (var rule in additions)
        {
            if (string.IsNullOrWhiteSpace(rule) || !existing.Add(rule))
            {
                continue;
            }

            // M1: reject rules that do not round-trip cleanly — a malformed rule litters the file.
            // Parse uses the trimmed form to get the canonical representation, then compare with
            // the original (untrimmed) to catch extra whitespace and other non-canonical forms.
            var parsed = PermissionRule.Parse(rule.Trim());
            var normalized = parsed.ToRuleString();
            if (!string.Equals(normalized, rule, StringComparison.Ordinal))
            {
                logger?.LogWarning(
                    "Skipping malformed permission rule '{Rule}'; it does not round-trip through the parser (normalized: '{Normalized}')",
                    rule,
                    normalized);
                continue;
            }

            array.Add(rule);
        }

        permissions[key] = array;
    }

    /// <summary>
    /// Persists a hook enable/disable override keyed by content hash to the user settings file.
    /// When <paramref name="enabled"/> is <see langword="false"/>, the hash is added to
    /// <c>hookDisabledHashes</c>; when <see langword="true"/>, it is removed. Atomic (temp file + move),
    /// preserving all other keys. This is what <c>/hooks enable</c> and <c>/hooks disable</c> write.
    /// </summary>
    public static void SetHookEnabled(string hookHash, bool enabled, string? userSettingsDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hookHash);

        var homeDir = userSettingsDir
            ?? Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(homeDir, ".coda");
        var file = Path.Combine(dir, "settings.json");

        JsonObject root;
        try
        {
            root = (File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null) ?? new JsonObject();
        }
        catch (JsonException)
        {
            // Corrupt settings file: abort rather than truncating the user's other settings keys.
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable settings: fail silently.
            return;
        }

        var disabledArray = root["hookDisabledHashes"] as JsonArray ?? [];
        var hashes = new HashSet<string>(
            disabledArray.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                         .Where(s => s is not null)!,
            StringComparer.Ordinal);

        if (!enabled)
        {
            hashes.Add(hookHash);
        }
        else
        {
            hashes.Remove(hookHash);
        }

        if (hashes.Count > 0)
        {
            root["hookDisabledHashes"] = new JsonArray(
                hashes.Order().Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
        }
        else
        {
            root.Remove("hookDisabledHashes");
        }

        Directory.CreateDirectory(dir);
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(file, json);
    }

    /// <summary>
    /// Persist the reasoning effort level <b>for a specific (provider, model) pair</b>
    /// under the <c>effortByModel</c> object (e.g. <c>"github-copilot/gpt-5.6-sol"</c> →
    /// <c>"high"</c>), preserving all other settings keys. A <see langword="null"/>
    /// <paramref name="effort"/> removes the key (reverts to the "auto" default).
    /// Atomic (temp file + move).
    /// </summary>
    public static void SetUserEffortForModel(string providerId, string model, string? effort, string? userSettingsDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var homeDir = userSettingsDir
            ?? Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(homeDir, ".coda");
        var file = Path.Combine(dir, "settings.json");

        JsonObject root;
        try
        {
            root = (File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null) ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        var key = $"{providerId}/{model}";
        var byModel = root["effortByModel"] as JsonObject ?? new JsonObject();
        if (effort is null)
        {
            byModel.Remove(key);
        }
        else
        {
            byModel[key] = effort;
        }

        if (byModel.Count > 0)
        {
            root["effortByModel"] = byModel;
        }
        else
        {
            root.Remove("effortByModel");
        }

        Directory.CreateDirectory(dir);
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(file, json);
    }

    private static void ApplyKey(JsonObject root, string key, string? value)
    {
        if (value is null)
        {
            return; // leave unchanged
        }

        if (value.Length == 0)
        {
            root.Remove(key); // explicit clear
            return;
        }

        root[key] = value;
    }
}

