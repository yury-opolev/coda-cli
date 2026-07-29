using System.Text.Json;
using Coda.Agent.Hooks;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Plugins;

/// <summary>
/// Loads plugin-contributed hooks from files listed in the manifest's <c>hooks</c> array.
/// Each file has the same JSON structure as the <c>hooks</c> section of <c>settings.json</c>:
/// an object mapping event names to arrays of hook entries.
/// <para>
/// Hooks inherit the plugin's installation scope: project-installed plugins contribute
/// project-scoped hooks (requiring explicit trust); user-installed plugins contribute
/// user-scoped hooks (trusted implicitly). The <see cref="UserHook.PluginOrigin"/> is
/// populated so updating the plugin changes the content hash and re-prompts trust.
/// </para>
/// </summary>
public static class PluginHookLoader
{
    /// <summary>
    /// Loads all hooks declared by a single plugin. Returns an empty list if the plugin is
    /// disabled, has no manifest, or has no hook files.
    /// </summary>
    /// <param name="plugin">The plugin to load hooks from.</param>
    /// <param name="workingDirectory">
    /// Used to determine whether the plugin is project-installed (→ <see cref="HookScope.Project"/>)
    /// or user-installed (→ <see cref="HookScope.User"/>).
    /// </param>
    /// <param name="userCodaDir">
    /// Override for the user-level <c>.coda</c> directory. Defaults to <c>~/.coda</c>.
    /// </param>
    /// <param name="logger">Optional diagnostic logger.</param>
    public static IReadOnlyList<UserHook> Load(
        PluginInfo plugin,
        string workingDirectory,
        string? userCodaDir = null,
        ILogger? logger = null)
    {
        if (!plugin.IsEnabled) return [];
        if (plugin.Manifest?.Hooks is not { Count: > 0 } hookPaths) return [];

        var scope = DetermineScope(plugin, workingDirectory);
        var origin = (plugin.Name, plugin.Version);
        var hooks = new List<UserHook>();

        foreach (var relativePath in hookPaths)
        {
            var resolved = PluginResourceLoader.ResolvePath(plugin, relativePath);

            // Containment check: hook files must live inside the plugin directory.
            if (!PluginResourceLoader.IsContained(resolved, plugin.Directory))
            {
                logger?.LogError(
                    "Plugin '{Plugin}': hook path '{Path}' escapes the plugin directory — skipped.",
                    plugin.Name, relativePath);
                continue;
            }

            if (!File.Exists(resolved))
            {
                logger?.LogWarning(
                    "Plugin '{Plugin}': hook file '{Path}' not found — skipped.",
                    plugin.Name, resolved);
                continue;
            }

            try
            {
                var fileHooks = ParseHookFile(resolved, plugin.Name, scope, origin, logger);
                hooks.AddRange(fileHooks);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                logger?.LogError(
                    "Plugin '{Plugin}': failed to parse hook file '{Path}': {Message}",
                    plugin.Name, resolved, ex.Message);
            }
        }

        return hooks;
    }

    private static IReadOnlyList<UserHook> ParseHookFile(
        string path,
        string pluginName,
        HookScope scope,
        (string Name, string Version) origin,
        ILogger? logger)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            logger?.LogWarning(
                "Plugin '{Plugin}': hook file '{Path}' must be a JSON object — skipped.",
                pluginName, path);
            return [];
        }

        var hooks = new List<UserHook>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var eventName = property.Name;
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in property.Value.EnumerateArray())
            {
                var hook = ParseHookEntry(eventName, entry, scope, origin, pluginName, path, logger);
                if (hook is not null)
                {
                    hooks.Add(hook);
                }
            }
        }

        return hooks;
    }

    private static UserHook? ParseHookEntry(
        string eventName,
        JsonElement entry,
        HookScope scope,
        (string Name, string Version) origin,
        string pluginName,
        string path,
        ILogger? logger)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            logger?.LogWarning(
                "Plugin '{Plugin}': hook entry in '{Path}' event '{Event}' is not an object — skipped.",
                pluginName, path, eventName);
            return null;
        }

        var command = entry.TryGetProperty("command", out var cmd) ? cmd.GetString() : null;
        var url = entry.TryGetProperty("url", out var u) ? u.GetString() : null;
        var prompt = entry.TryGetProperty("prompt", out var p) ? p.GetString() : null;
        var agentType = entry.TryGetProperty("agent", out var ag) ? ag.GetString() : null;
        var handlerType = entry.TryGetProperty("type", out var ht) ? ht.GetString() : null;
        var matcher = entry.TryGetProperty("matcher", out var m) ? m.GetString() : null;
        var unattendedDecision = entry.TryGetProperty("unattendedDecision", out var ud) ? ud.GetString() : null;
        int? timeoutSeconds = null;
        if (entry.TryGetProperty("timeoutSeconds", out var ts) && ts.TryGetInt32(out var tsVal))
        {
            timeoutSeconds = tsVal;
        }

        bool? failOpen = null;
        if (entry.TryGetProperty("failOpen", out var fo) && fo.ValueKind == JsonValueKind.True)
        {
            failOpen = true;
        }
        else if (entry.TryGetProperty("failOpen", out fo) && fo.ValueKind == JsonValueKind.False)
        {
            failOpen = false;
        }

        // Derive handler type when not explicit.
        handlerType ??= command is not null ? "command"
            : url is not null ? "http"
            : prompt is not null ? "prompt"
            : agentType is not null ? "agent"
            : null;

        if (handlerType is null)
        {
            logger?.LogWarning(
                "Plugin '{Plugin}': hook in '{Path}' event '{Event}' has no recognizable handler — skipped.",
                pluginName, path, eventName);
            return null;
        }

        return new UserHook(
            Event: eventName,
            Command: command,
            Matcher: matcher,
            TimeoutSeconds: timeoutSeconds,
            FailOpen: failOpen,
            UnattendedDecision: unattendedDecision,
            HandlerType: handlerType,
            Url: url,
            HookPrompt: prompt,
            AgentType: agentType,
            Enabled: true,
            Scope: scope,
            PluginOrigin: origin);
    }

    private static HookScope DetermineScope(PluginInfo plugin, string workingDirectory)
    {
        // A plugin is project-scoped if its directory is anywhere inside the workspace root —
        // including .coda/plugins/ subdirectories and foreign .claude-plugin/ manifests.
        // Append the separator so a workspace at "C:\proj" does not match "C:\proj-extra\...".
        var workspacePath = Path.GetFullPath(workingDirectory) + Path.DirectorySeparatorChar;
        var pluginDir = Path.GetFullPath(plugin.Directory) + Path.DirectorySeparatorChar;

        return pluginDir.StartsWith(workspacePath, StringComparison.OrdinalIgnoreCase)
            ? HookScope.Project
            : HookScope.User;
    }
}
