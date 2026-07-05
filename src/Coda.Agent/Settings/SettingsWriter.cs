using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

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
        var tmp = Path.Combine(dir, $".settings.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, json);
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>
    /// Set the persisted default provider and/or model. A <see langword="null"/> value
    /// leaves that key unchanged; an empty string removes it (reset to default).
    /// </summary>
    public static void SetUserDefaults(string? defaultProvider = null, string? defaultModel = null, string? userSettingsDir = null)
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
        ApplyKey(root, "defaultModel", defaultModel);

        Directory.CreateDirectory(dir);

        // Atomic write: serialize to a temp file in the same directory, then replace.
        // A crash or concurrent writer can't truncate settings.json (which also holds
        // the user's permissions/hooks/lspServers).
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var tmp = Path.Combine(dir, $".settings.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, json);
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>
    /// Persist the default model <b>for a specific provider</b> under the
    /// <c>defaultModelByProvider</c> object (e.g. <c>github-copilot -&gt; claude-opus-4.8</c>),
    /// preserving all other providers' entries and all other settings keys. This is what the
    /// <c>/model</c> command writes so a model belongs to its provider. Atomic (temp file + move).
    /// </summary>
    public static void SetUserDefaultModelForProvider(string providerId, string model, string? userSettingsDir = null)
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

        var byProvider = root["defaultModelByProvider"] as JsonObject ?? new JsonObject();
        byProvider[providerId] = model;
        root["defaultModelByProvider"] = byProvider;

        Directory.CreateDirectory(dir);
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var tmp = Path.Combine(dir, $".settings.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, json);
        File.Move(tmp, file, overwrite: true);
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
        var tmp = Path.Combine(dir, $".settings.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, json);
        File.Move(tmp, file, overwrite: true);
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
